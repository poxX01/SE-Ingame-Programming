using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using SpaceEngineers.Game.ModAPI.Ingame;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using VRage;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ObjectBuilders.Definitions;
using VRageMath;

namespace IngameScript {
    partial class Program : MyGridProgram {
        //TODO: Handle minimum RPM speeds automatically for multiple sun orbit speeds(= currentRPM must not be much lower than sun orbit speed)
        //Perhaps via a precision mode once positive deltas become the size of 0.001f or something?
        //Consequentially, the SUN_EXPOSURE_PAUSE_THRESHOLD value needs to be scaled accordingly
        //and the concept of const MINIMUM and MAXIMUM angular speeds made variable instead
        //Possibly introduce our rotation axes as bitflags;
        //TODO: Expand sun algorithm data gathering, besides the sunPlaneNormal, we need the sun movement speed and the rotation direction around sunPlaneNormal (3 data points total)
        //TODO: Setup IGC to transmit sunOrbitPoints and to receive commands. Also always send a reply confirming the reception of a transmission.

        //Leave empty to have the program use the first functional one found on the same construct
        const string NAME_REFERENCE_SOLAR_PANEL = "";

        const float SUN_EXPOSURE_PAUSE_THRESHOLD = -0.2f; //if exposure delta ever goes below this value in one run, Pause() is called
        const int PAUSE_PERIOD = 36; //in multiples of 100/60~1.6667s, leave at 36 for a duration of 1min
        const float MAXIMUM_POSSIBLE_SUN_ANGULAR_SPEED = (float)(1 / 600 * 2 * Math.PI); //in Rad/s, lowest is 10min (1orbit per 600s)
        const float MINIMUM_ANGULAR_SPEED = 0.0014f; //Setting gyro speeds via programming is done in Rad/s, max speed is 2Pi
        const float PRECISION_THRESHOLD = 0.999985f; //150W off perfect readout precision in a small panel

        const UpdateFrequency FREQUENCY_PAUSE = UpdateFrequency.Update100;
        const UpdateFrequency FREQUENCY_SEARCH = UpdateFrequency.Update10 | UpdateFrequency.Update100;
        const UpdateType AUTOMATIC_UPDATE_TYPE = UpdateType.Update100 | UpdateType.Update10 | UpdateType.Update1 | UpdateType.Once;
        float maxPossibleOutput; //in MW
        MyIni storageIni = new MyIni();

        int currentRoutineRepeats;
        float previousSunExposure;
        float mostRecentSunExposure; //1 for perfect exposure, 0 for absolutely no sun on our reference panel
        float exposureDelta;

        //Storage for DetermineSunPlaneNormal()
        float minimumAngularSpeed;
        bool haveNotInvertedYetThisAxis = true;
        bool exposureDeltaGreaterThanZeroOnLastRun = false;
        int[] currentSigns = { 1, 1 };
        Vector3D sunOrbitPointOne;

        //Storage for DetermineSunOrbitDirection
        int currentStage = 1;
        bool exposureIncreasedDuringIdle;
        bool exposureIncreasedDuringRotation;

        IMySolarPanel referenceSolarPanel;
        List<Gyroscope> registeredGyros = new List<Gyroscope>();
        Gyroscope.PrincipalAxis[] rotationAxes;
        Base6Directions.Direction[] dotProductFactorDirections;
        SunOrbit sunOrbit = new SunOrbit();

        Gyroscope.PrincipalAxis currentRotationAxis = Gyroscope.PrincipalAxis.Pitch;
        UpdateType currentUpdateSource;
        Routines currentRoutine = Routines.None;
        Routines routineToResumeOn = Routines.None; //Will resume on this routime after pause
        Dictionary<Routines, Action> dicRoutines;
        Dictionary<string, Action> dicCommands;
        public enum Routines { None, Pause, DetermineSunPlaneNormal, AlignToSunPlaneNormal, PrepareSunOrbitDirectionMeasure, DetermineSunOrbitDirection, DetermineSunOrbitalPeriod }
        public class Gyroscope {
            public IMyGyro gyroBlock;
            protected MyTuple<PrincipalAxis, int> pitchCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            protected MyTuple<PrincipalAxis, int> yawCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            protected MyTuple<PrincipalAxis, int> rollCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            public enum PrincipalAxis { Pitch, Yaw, Roll }
            public Gyroscope(IMyTerminalBlock originBlock, IMyGyro gyroBlock) {
                //Grid pivot axes: blue = Forward, red = Right, green = Up
                //Respective rotation axes for PYR via Right Hand Rule (direction of non-thumb fingers pointing counter-clockwise, positive rotation direction) are:
                //Pitch: Right (+rotates upward, -rotates downward)
                //Yaw: Down (+rotes right, -rotates left)
                //Roll: Forward (+tilts rightside, -tilts leftside)
                //Gyros are odd. Assigning a negative value to pitch makes it a positive value in the terminal and vice versa, meaning that in programming the change of pitch is inverted
                //Therefore, our pitch axis in programming for gyros has to use the left hand rule. Truly horrendous.
                //To stick to the right hand rule persistently, we'll invert the signs for Right and Left in DetermineMatchingRotationAxis().
                Vector3D originPitchAxis = originBlock.WorldMatrix.Right;
                Vector3D originYawAxis = originBlock.WorldMatrix.Down;
                Vector3D originRollAxis = originBlock.WorldMatrix.Forward;
                Vector3D currentComparisonVector;
                foreach(Base6Directions.Direction direction in Base6Directions.EnumDirections) {
                    currentComparisonVector = gyroBlock.WorldMatrix.GetDirectionVector(direction);
                    if(currentComparisonVector == originPitchAxis) pitchCorrespondingAxis = DetermineMatchingRotationAxis(direction);
                    else if(currentComparisonVector == originYawAxis) yawCorrespondingAxis = DetermineMatchingRotationAxis(direction);
                    else if(currentComparisonVector == originRollAxis) rollCorrespondingAxis = DetermineMatchingRotationAxis(direction);
                }
                this.gyroBlock = gyroBlock;
            }
            private static MyTuple<PrincipalAxis, int> DetermineMatchingRotationAxis(Base6Directions.Direction direction) {
                int sign = 1;
                switch(direction) {
                    case Base6Directions.Direction.Right: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Pitch, -sign);
                    case Base6Directions.Direction.Down: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Yaw, sign);
                    case Base6Directions.Direction.Forward: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Roll, sign);
                    case Base6Directions.Direction.Left: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Pitch, sign);
                    case Base6Directions.Direction.Up: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Yaw, -sign);
                    case Base6Directions.Direction.Backward: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Roll, -sign);
                    default: throw new Exception("Gyroscope.DetermineMatchingRotationAxis() was called with no valid Base6Directions.Direction.");
                }
            }
            public static void DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction directionToAlignToTarget,
                out PrincipalAxis[] rotationAxes, out Base6Directions.Direction[] dotProductFactorDirections) {
                rotationAxes = new PrincipalAxis[2];
                dotProductFactorDirections = new Base6Directions.Direction[2];
                switch(directionToAlignToTarget) {
                    case Base6Directions.Direction.Forward:
                        rotationAxes[0] = PrincipalAxis.Pitch;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Up;
                        rotationAxes[1] = PrincipalAxis.Yaw;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Right;
                        break;
                    case Base6Directions.Direction.Backward:
                        rotationAxes[0] = PrincipalAxis.Pitch;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Down;
                        rotationAxes[1] = PrincipalAxis.Yaw;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Left;
                        break;
                    case Base6Directions.Direction.Right:
                        rotationAxes[0] = PrincipalAxis.Yaw;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Backward;
                        rotationAxes[1] = PrincipalAxis.Roll;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Down;
                        break;
                    case Base6Directions.Direction.Left:
                        rotationAxes[0] = PrincipalAxis.Yaw;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Forward;
                        rotationAxes[1] = PrincipalAxis.Roll;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Up;
                        break;
                    case Base6Directions.Direction.Up:
                        rotationAxes[0] = PrincipalAxis.Pitch;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Backward;
                        rotationAxes[1] = PrincipalAxis.Roll;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Right;
                        break;
                    case Base6Directions.Direction.Down:
                        rotationAxes[0] = PrincipalAxis.Pitch;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Forward;
                        rotationAxes[1] = PrincipalAxis.Roll;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Left;
                        break;
                }
            }
            public static bool AlignToTarget(Vector3D target, MatrixD anchorToAlignWorldMatrix, PrincipalAxis[] rotationAxes,
                Base6Directions.Direction[] dotProductFactorDirections, List<Gyroscope> gyroList,
                float alignmentSuccessThreshold = 0.0001f, float speedLimitInRadPS = 0.75f) {
                //TODO: Make this work when alignment into the opposite direction is true
                //As this method is designed to be run on UpdateFrequency.Update1, it is optimized for performance. Maybe the float instantiation could be moved however.
                //Aligning to a target with a certain vector requires the two rotation axes used to align need to be in perpendicular position to the target, i.e. a 0 dot product
                float rotationRemainder0 = (float)anchorToAlignWorldMatrix.GetDirectionVector(dotProductFactorDirections[0]).Dot(target) * 10;
                float rotationRemainder1 = (float)anchorToAlignWorldMatrix.GetDirectionVector(dotProductFactorDirections[1]).Dot(target) * 10;
                rotationRemainder0 = rotationRemainder0 > speedLimitInRadPS ? speedLimitInRadPS : rotationRemainder0 < -speedLimitInRadPS ? -speedLimitInRadPS : rotationRemainder0;
                rotationRemainder1 = rotationRemainder1 > speedLimitInRadPS ? speedLimitInRadPS : rotationRemainder1 < -speedLimitInRadPS ? -speedLimitInRadPS : rotationRemainder1;
                for(int i = 0; i < gyroList.Count; i++) {
                    gyroList[i].SetRotation(rotationAxes[0], rotationRemainder0);
                    gyroList[i].SetRotation(rotationAxes[1], rotationRemainder1);
                }
                return Math.Abs(rotationRemainder0) < alignmentSuccessThreshold && Math.Abs(rotationRemainder1) < alignmentSuccessThreshold;
            }
            protected void Enable() {
                gyroBlock.Enabled = true;
                gyroBlock.GyroOverride = true;
                gyroBlock.GyroPower = 1;
            }
            protected void SetRotation(MyTuple<PrincipalAxis, int> rotationAxis, float radPerSecond) {
                Enable();
                switch(rotationAxis.Item1) {
                    case PrincipalAxis.Pitch:
                        gyroBlock.Pitch = rotationAxis.Item2 * radPerSecond;
                        break;
                    case PrincipalAxis.Yaw:
                        gyroBlock.Yaw = rotationAxis.Item2 * radPerSecond;
                        break;
                    case PrincipalAxis.Roll:
                        gyroBlock.Roll = rotationAxis.Item2 * radPerSecond;
                        break;
                }
            }
            public void SetRotation(PrincipalAxis axisToRotateAround, float radPerSecond) {
                switch(axisToRotateAround) {
                    case PrincipalAxis.Pitch:
                        SetRotation(pitchCorrespondingAxis, radPerSecond);
                        break;
                    case PrincipalAxis.Yaw:
                        SetRotation(yawCorrespondingAxis, radPerSecond);
                        break;
                    case PrincipalAxis.Roll:
                        SetRotation(rollCorrespondingAxis, radPerSecond);
                        break;
                }
            }
            public void StopRotation() {
                Enable();
                gyroBlock.Pitch = 0;
                gyroBlock.Yaw = 0;
                gyroBlock.Roll = 0;
                gyroBlock.GyroOverride = false;
            }
        }
        public class Logger {
            public const UpdateType AUTOMATIC_UPDATE_TYPE = UpdateType.Update100 | UpdateType.Update10 | UpdateType.Update1 | UpdateType.Once;
            public const UpdateType MANUAL_UPDATE_TYPE = UpdateType.Terminal | UpdateType.Trigger;
            StringBuilder logger;


        }
        public sealed class SunOrbit {
            private Vector3D _planeNormal;
            private int _direction; //1 is clockwise, -1 is counter clockwise, rotation direction around planeNormal (right hand rule, our Down aligned with planeNormal)
            private float _orbitalPeriod; //in minutes
            public Vector3D PlaneNormal {
                get { return _planeNormal; }
                set { _planeNormal = Vector3D.Normalize(value); }
            }
            public int Direction {
                get { return _direction; }
                set { _direction = value == 1 || value == -1 ? value : 0; }
            }
            public float OrbitalPeriod {
                get { return _orbitalPeriod; }
                set { _orbitalPeriod = value >= 10 ? value : 0; }
            }
            public float PrecisionThreshold { get; private set; }

            private const string SECTION_NAME = "Sun Orbit";
            private const string PLANE_NORMAL_KEY = "Plane normal";
            private const string DIRECTION_KEY = "Orbit direction";
            private const string ORBITAL_PERIOD_KEY = "Orbital period";

            public SunOrbit(MyIni storageIni = null) {
                if(storageIni is object) ReadFromIni(storageIni);
                PrecisionThreshold = PRECISION_THRESHOLD;
            }
            public bool IsMapped() {
                return !_planeNormal.IsZero() && _direction != 0 && _orbitalPeriod != 0;
            }
            public void ClearData() {
                //TODO: Allow for modular clearing, possibly make it user friendly so it can be done via arguments
                _planeNormal = Vector3D.Zero;
                _direction = 0;
                _orbitalPeriod = 0;
            }
            public void WriteToIni(MyIni storageIni) {
                storageIni.Set(SECTION_NAME, PLANE_NORMAL_KEY, _planeNormal.ToString());
                storageIni.Set(SECTION_NAME, DIRECTION_KEY, _direction);
                storageIni.Set(SECTION_NAME, ORBITAL_PERIOD_KEY, _orbitalPeriod);
            }
            public void ReadFromIni(MyIni storageIni) {
                if(storageIni.ContainsSection(SECTION_NAME)) {
                    Vector3D.TryParse(storageIni.Get(SECTION_NAME, PLANE_NORMAL_KEY).ToString(), out _planeNormal);
                    _direction = storageIni.Get(SECTION_NAME, DIRECTION_KEY).ToInt32();
                    _orbitalPeriod = storageIni.Get(SECTION_NAME, ORBITAL_PERIOD_KEY).ToSingle();
                }
            }
        }
        public Program() {
            InitializeBlocks();
            if(storageIni.TryParse(Storage)) {
                currentRoutine = (Routines)storageIni.Get("storage", "currentRoutine").ToInt32();
                routineToResumeOn = (Routines)storageIni.Get("storage", "routineToResumeOn").ToInt32();
                Vector3D.TryParse(storageIni.Get("storage", "sunOrbitPointOne").ToString(), out sunOrbitPointOne);
                sunOrbit.ReadFromIni(storageIni);
            }

            dicRoutines = new Dictionary<Routines, Action>() {
                {Routines.None, () => { } },
                {Routines.Pause, () => Pause()},
                {Routines.DetermineSunPlaneNormal, () => DetermineSunPlaneNormal()},
                {Routines.AlignToSunPlaneNormal, () => {
                    if(Gyroscope.AlignToTarget(sunOrbit.PlaneNormal, referenceSolarPanel.WorldMatrix, rotationAxes, dotProductFactorDirections, registeredGyros))
                        ChangeCurrentRoutine(Routines.DetermineSunOrbitDirection);
                }},
                {Routines.PrepareSunOrbitDirectionMeasure, () => {
                    UpdateExposureValues();
                    foreach (Gyroscope gyro in registeredGyros) gyro.SetRotation(Gyroscope.PrincipalAxis.Yaw, 0.11f);
                    if (mostRecentSunExposure >= maxPossibleOutput * 0.45f && mostRecentSunExposure <= maxPossibleOutput * 0.55f) ChangeCurrentRoutine(Routines.DetermineSunOrbitDirection);
                }},
                {Routines.DetermineSunOrbitDirection, () => DetermineSunOrbitDirection()},

            };
            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                {"Run", () => {if (!sunOrbit.IsMapped()){
                     if (sunOrbit.PlaneNormal.IsZero()) ChangeCurrentRoutine(Routines.DetermineSunPlaneNormal);
                     else if (sunOrbit.Direction == 0) ChangeCurrentRoutine(Routines.AlignToSunPlaneNormal);
                     else if (sunOrbit.OrbitalPeriod < 10) ChangeCurrentRoutine(Routines.DetermineSunOrbitalPeriod);
                    }}},
                {"Halt", () => ChangeCurrentRoutine(Routines.None) },
                {"Reinitialize", () => InitializeBlocks() },
                {"ClearData", () => sunOrbit.ClearData() },
            };
        }
        public void Save() {
            storageIni.Clear();
            storageIni.Set("storage", "currentRoutine", (int)currentRoutine);
            storageIni.Set("storage", "routineToResumeOn", (int)currentRoutine);
            storageIni.Set("storage", "sunPlaneOrbitPointOne", sunOrbitPointOne.ToString());
            sunOrbit.WriteToIni(storageIni);
            Storage = storageIni.ToString();
        }
        public void Main(string argument, UpdateType updateSource) {
            Echo(Runtime.LastRunTimeMs.ToString());
            currentUpdateSource = updateSource;
            if((updateSource & AUTOMATIC_UPDATE_TYPE) == 0) {
                if(argument.Length > 0) {
                    Action currentCommand;
                    if(dicCommands.TryGetValue(argument, out currentCommand)) currentCommand();
                    else {
                        StringBuilder printable = new StringBuilder("Invalid command was passed as an argument. Valid arguments are:\n");
                        foreach(string key in dicCommands.Keys) {
                            if(key == "Run") printable.AppendLine(key + " (default, if no command specified)");
                            else printable.AppendLine(key);
                        }
                        Echo(printable.ToString());
                    }
                }
                else dicCommands["Run"]();
            }
            else dicRoutines[currentRoutine]();
        }
        public void Pause() {
            switch(currentRoutineRepeats) {
                case PAUSE_PERIOD - 1:
                    ChangeCurrentRoutine(routineToResumeOn);
                    break;
                default: currentRoutineRepeats += 1; break;
            }
        }
        public void ChangeCurrentRoutine(Routines targetRoutine) {
            UpdateFrequency updateFrequency;
            currentStage = 1;
            routineToResumeOn = Routines.None;
            currentRoutineRepeats = 0;
            for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].StopRotation();
            switch(targetRoutine) {
                case Routines.None:
                    updateFrequency = UpdateFrequency.None;
                    break;
                case Routines.Pause:
                    routineToResumeOn = currentRoutine;
                    updateFrequency = UpdateFrequency.Update100;
                    break;
                case Routines.DetermineSunPlaneNormal:
                    updateFrequency = UpdateFrequency.Update10 | UpdateFrequency.Update100;
                    break;
                case Routines.AlignToSunPlaneNormal:
                    Gyroscope.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Down, out rotationAxes, out dotProductFactorDirections);
                    updateFrequency = UpdateFrequency.Update1;
                    break;
                case Routines.DetermineSunOrbitDirection:
                    updateFrequency = UpdateFrequency.Update100;
                    break;
                default:
                    updateFrequency = UpdateFrequency.Update10;
                    break;
            }
            currentRoutine = targetRoutine;
            Runtime.UpdateFrequency = updateFrequency;
        }
        public void UpdateExposureValues() {
            previousSunExposure = mostRecentSunExposure;
            mostRecentSunExposure = referenceSolarPanel.MaxOutput / maxPossibleOutput;
            exposureDelta = mostRecentSunExposure - previousSunExposure;
        }
        public void DetermineSunPlaneNormal() {
            //Two points are determined where sunExposure >= precision threshold
            UpdateExposureValues();
            if(mostRecentSunExposure >= PRECISION_THRESHOLD) {
                //TODO: Rework this to initiate determining the sunOrbitalPeriod & direction after determining sunPlaneNormal depending on what's missing (as ideally data can be cleared out modularly)
                if(sunOrbitPointOne.IsZero()) {
                    sunOrbitPointOne = referenceSolarPanel.WorldMatrix.Forward;
                    ChangeCurrentRoutine(Routines.Pause);
                }
                else {
                    sunOrbit.PlaneNormal = sunOrbitPointOne.Cross(referenceSolarPanel.WorldMatrix.Forward);
                    ChangeCurrentRoutine(Routines.None);
                }
                return;
            }
            if(exposureDelta < 0) {
                if(exposureDelta > SUN_EXPOSURE_PAUSE_THRESHOLD) {
                    if(haveNotInvertedYetThisAxis) {
                        currentSigns[(int)currentRotationAxis] *= -1;
                        haveNotInvertedYetThisAxis = false;
                    }
                    else {
                        exposureDeltaGreaterThanZeroOnLastRun = false;
                        haveNotInvertedYetThisAxis = true;
                        currentRotationAxis = 1 - currentRotationAxis;
                        return;
                    }
                }
                else {
                    ChangeCurrentRoutine(Routines.Pause);
                    return;
                }
            }
            else if((currentUpdateSource & UpdateType.Update100) != 0 && exposureDelta > 0) {
                if(exposureDeltaGreaterThanZeroOnLastRun) haveNotInvertedYetThisAxis = false;
                else exposureDeltaGreaterThanZeroOnLastRun = true;
            }
            //TODO: Make this value non-constant and able to adapt to current sunExposure progress
            float currentAngularMomentum = Math.Max((1 - mostRecentSunExposure) / 2, MINIMUM_ANGULAR_SPEED) * currentSigns[(int)currentRotationAxis];
            for(int i = 0; i < registeredGyros.Count; i++) { registeredGyros[i].SetRotation(currentRotationAxis, currentAngularMomentum); }
        }
        public void DetermineSunOrbitDirection() {
            //TEST: workey?
            //Rotation direction will be:
            //+, +: -1     +, -: 1      -, +: 1     -, -: -1
            UpdateExposureValues();
            if(referenceSolarPanel.WorldMatrix.Down.Dot(sunOrbit.PlaneNormal) < 0.999f) ChangeCurrentRoutine(Routines.AlignToSunPlaneNormal);
            if(mostRecentSunExposure >= maxPossibleOutput * 0.3f && mostRecentSunExposure <= maxPossibleOutput * 0.7f) {
                bool previousMeasure;
                if(exposureDelta == 0) return;
                switch(currentStage) {
                    case 1:
                        previousMeasure = exposureIncreasedDuringIdle;
                        exposureIncreasedDuringIdle = exposureDelta > 0;
                        if(exposureIncreasedDuringIdle != previousMeasure) currentRoutineRepeats = 0;
                        if(currentRoutineRepeats >= 3) {
                            foreach(Gyroscope gyro in registeredGyros) gyro.SetRotation(Gyroscope.PrincipalAxis.Yaw, 0.033f);
                            currentStage = 2;
                            currentRoutineRepeats = 0;
                        }
                        break;
                    case 2:
                        previousMeasure = exposureIncreasedDuringRotation;
                        exposureIncreasedDuringRotation = exposureDelta > 0;
                        if(exposureIncreasedDuringRotation != previousMeasure) currentRoutineRepeats = 0;
                        if(currentRoutineRepeats >= 3) {
                            sunOrbit.Direction = exposureIncreasedDuringIdle ^ exposureIncreasedDuringRotation ? 1 : -1;
                            ChangeCurrentRoutine(Routines.DetermineSunOrbitalPeriod);
                        }
                        break;
                }
                currentRoutineRepeats++;
            }
            else ChangeCurrentRoutine(Routines.PrepareSunOrbitDirectionMeasure);
        }
        public void DetermineSunOrbitalPeriod() {

        }
        public void InitializeBlocks() {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType(blocks, block => block.IsSameConstructAs(Me) && block.IsFunctional);
            referenceSolarPanel = GetBlock<IMySolarPanel>(NAME_REFERENCE_SOLAR_PANEL, blocks);
            maxPossibleOutput = referenceSolarPanel.CubeGrid.GridSizeEnum == MyCubeSize.Small ? 0.04f : 0.16f; //in MW

            registeredGyros.Clear();
            List<IMyGyro> gyroList = new List<IMyGyro>();
            GridTerminalSystem.GetBlocksOfType(gyroList, gyro => gyro.IsSameConstructAs(Me));
            foreach(IMyGyro gyro in gyroList) registeredGyros.Add(new Gyroscope(referenceSolarPanel, gyro));
        }
        public T GetBlock<T>(string blockName = "", List<IMyTerminalBlock> blocks = null) {
            var blocksLocal = blocks ?? new List<IMyTerminalBlock>(); ;
            T myBlock = (T)GridTerminalSystem.GetBlockWithName(blockName);
            if(!(myBlock is object) && !(blocks is object)) GridTerminalSystem.GetBlocks(blocksLocal);
            if(!(myBlock is object)) myBlock = (T)blocksLocal.Find(block => block.GetType().Name == typeof(T).Name.Substring(1));
            if(myBlock is object) return myBlock;
            else throw new Exception($"An owned block of type {typeof(T).Name} does not exist in the provided block list.");
        }
    }
}
