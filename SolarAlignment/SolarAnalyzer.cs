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
        #region CopyIngame
        //TODO: Setup IGC to transmit sunOrbitPoints and to receive commands. Also always send a reply confirming the reception of a transmission.

        //Leave empty to have the program use the first functional one found on the same construct
        const string NAME_REFERENCE_SOLAR_PANEL = "";
        const string NAME_LCD = "";

        const float MINIMUM_SUN_SPEED = (float)(1 / 600 * 2 * Math.PI);
        const float SUN_EXPOSURE_PAUSE_THRESHOLD = -0.2f; //if exposure delta ever goes below this value in one run, Pause() is called
        const float MINIMUM_ANGULAR_SPEED = 0.0014f; //Setting gyro speeds via programming is done in Rad/s, max speed is 2Pi
        //TODO: LOW PRIORITY: Support a large panel and modded ones (ratios may be different, especially with rounding throughout code)
        //E.g. introduce (in custom data) an entry for panel max output
        //const float PRECISION_THRESHOLD = 0.999985f; //150W off perfect readout precision in a small panel
        const float PRECISION_THRESHOLD = 1;
        const float EXPOSURE_OFFSET_THRESHOLD = PRECISION_THRESHOLD - 0.0001f; //TODO: possibly scale this with sun speed? This is our best proxy for knowing we still have nearly 100% sun access with no obstruction

        const UpdateType AUTOMATIC_UPDATE_TYPE = UpdateType.Update100 | UpdateType.Update10 | UpdateType.Update1 | UpdateType.Once;
        float maxPossibleOutput; //in MW
        //TODO: Initiate custom data if not empty and set default values there, then read them at the end of Program()
        bool determineOrbitPlaneNormalManually = true;

        int currentRoutineRepeats;
        float previousSunExposure;
        float mostRecentSunExposure; //1 for perfect exposure, 0 for absolutely no sun on our reference panel
        float exposureDelta;

        //Storage for DetermineSunPlaneNormal()
        PrincipalAxis currentRotationAxis = PrincipalAxis.Pitch;
        bool haveNotInvertedYetThisAxis = true;
        bool exposureDeltaGreaterThanZeroOnLastRun = false;
        int[] currentSigns = { 1, 1 };
        Vector3D sunOrbitPointOne;

        //Storage for PrepareDetermineSunOrbitDirection()
        //TODO: Remove this once rework is done
        float gyroSpeed;

        //Storage for AlignForwardToSun()
        Vector3D targetVector = new Vector3D();

        //Storage for DetermineSunOrbitDirection()
        int currentStage = 1;
        bool exposureIncreasedDuringIdle;
        bool exposureIncreasedDuringRotation;
        float[] exposureDeltaMeasurements = new float[5];

        //Storage for Pause()
        int targetRoutineRepeats = 36;

        //Storage for ObtainPreliminaryAngularSpeed
        float[] angleDeltaMeasurements = new float[20]; //measurements are in radians per second

        //Storage for DetermineSunAngularSpeed
        int targetDigit;
        float preliminaryAngularSpeed;

        //Storage for MANUAL LCD feedback
        StringBuilder lcdText = new StringBuilder($"Align towards the sun within 1 and target margin.\n\nTarget: {PRECISION_THRESHOLD}\nCurrent: ");
        readonly int lcdTextDefaultLength;
        IMyTextPanel lcd;

        IMySolarPanel referenceSolarPanel;
        List<Gyroscope> registeredGyros = new List<Gyroscope>();
        RotationHelper rotationHelper = new RotationHelper();
        SunOrbit sunOrbit = new SunOrbit(PRECISION_THRESHOLD);

        UpdateType currentUpdateSource;
        Routines currentRoutine;
        Routines routineToResumeOn; //Will resume on this routime after an intermediate routine (e.g. Pause and Prepare routines)
        MyIni storageIni = new MyIni();
        MyCommandLine _commandLine = new MyCommandLine();
        Dictionary<Routines, Action> dicRoutines;
        Dictionary<string, Action> dicCommands;
        public enum Routines {
            None,
            Pause,
            DetermineSunPlaneNormal,
            DetermineSunPlaneNormalManual,
            AlignPanelDownToSunPlaneNormal,
            AlignPanelToSun,
            PrepareDetermineSunOrbitDirection,
            DetermineSunOrbitDirection,
            ObtainPreliminaryAngularSpeed,
            DetermineSunOrbitAngularSpeed,
            Debug
        }
        public enum PrincipalAxis { Pitch, Yaw, Roll }
        public class RotationHelper {
            //The following two are mostly only useful for gyroscope rotation, as all 3 axes are available to us.
            //E.g. I want my RC.Forward to align to (0, 1, 0), I require the axes that aren't irrelevant in that rotation (Roll is), and those respective axes' planeNormals for the dot product measure
            private PrincipalAxis[] _rotationAxes = new PrincipalAxis[2]; //The gyroscope operates via Pitch/Yaw/Roll, so the respective axes are stored for a given rotation task.
            private Base6Directions.Direction[] _dotProductFactorDirections = new Base6Directions.Direction[2]; //As alignment is done by setting the dot product of 2 axes to 0, the directions for those two axes needs to be stored
            public PrincipalAxis[] RotationAxes { get { return _rotationAxes; } }
            public Base6Directions.Direction[] DotProductFactorDirections { get { return _dotProductFactorDirections; } }
            public Vector3D RotatedVectorClockwise { get; private set; }
            public Vector3D RotatedVectorCounterClockwise { get; private set; }
            public void DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction directionToAlignToAnyFutureTarget) {
                switch(directionToAlignToAnyFutureTarget) {
                    case Base6Directions.Direction.Forward:
                        _rotationAxes[0] = PrincipalAxis.Pitch;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Up;
                        _rotationAxes[1] = PrincipalAxis.Yaw;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Right;
                        break;
                    case Base6Directions.Direction.Backward:
                        _rotationAxes[0] = PrincipalAxis.Pitch;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Down;
                        _rotationAxes[1] = PrincipalAxis.Yaw;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Left;
                        break;
                    case Base6Directions.Direction.Right:
                        _rotationAxes[0] = PrincipalAxis.Yaw;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Backward;
                        _rotationAxes[1] = PrincipalAxis.Roll;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Down;
                        break;
                    case Base6Directions.Direction.Left:
                        _rotationAxes[0] = PrincipalAxis.Yaw;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Forward;
                        _rotationAxes[1] = PrincipalAxis.Roll;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Up;
                        break;
                    case Base6Directions.Direction.Up:
                        _rotationAxes[0] = PrincipalAxis.Pitch;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Backward;
                        _rotationAxes[1] = PrincipalAxis.Roll;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Right;
                        break;
                    case Base6Directions.Direction.Down:
                        _rotationAxes[0] = PrincipalAxis.Pitch;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Forward;
                        _rotationAxes[1] = PrincipalAxis.Roll;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Left;
                        break;
                }
            }
            public void GenerateRotatedNormalizedVectorsAroundAxisByAngle(Vector3D vectorToRotate, Vector3D rotationAxis, double angle) {
                vectorToRotate.Normalize();
                rotationAxis.Normalize();
                RotatedVectorClockwise = Vector3D.Transform(vectorToRotate, MatrixD.CreateFromQuaternion(QuaternionD.CreateFromAxisAngle(rotationAxis, angle)));
                RotatedVectorCounterClockwise = Vector3D.Transform(vectorToRotate, MatrixD.CreateFromQuaternion(QuaternionD.CreateFromAxisAngle(rotationAxis, -angle)));
            }
            public bool IsAlignedWithNormalizedTargetVector(Vector3D targetVec, Vector3D measureVec, float alignmentSuccessThreshold = 0.0001f) {
                return Vector3D.Dot(targetVec, measureVec) >= 1 - alignmentSuccessThreshold;
            }
        }
        public class Gyroscope {
            //Stores a gyroblock and the axes it would have to rotate under to align to a given target, assuming the block whose vectors to align are not the gyro itsself
            //e.g. align a solar panel, gyro is rotated 90 degrees on all axes relative to said panel --> actual rotation needs to occur on corresponding axes
            public IMyGyro gyroBlock;
            protected MyTuple<PrincipalAxis, int> pitchCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            protected MyTuple<PrincipalAxis, int> yawCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            protected MyTuple<PrincipalAxis, int> rollCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
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
            public static void DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction directionToAlignToFutureGivenTarget,
                out PrincipalAxis[] rotationAxes, out Base6Directions.Direction[] dotProductFactorDirections) {
                rotationAxes = new PrincipalAxis[2];
                dotProductFactorDirections = new Base6Directions.Direction[2];
                switch(directionToAlignToFutureGivenTarget) {
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
            public static bool AlignToTargetNormalizedVector(Vector3D target, MatrixD anchorToAlignWorldMatrix, RotationHelper rotationHelper, List<Gyroscope> gyroList,
                float alignmentSuccessThreshold = 0.0001f, float speedLimitInRadPS = 0.75f) {
                //TODO: Make this work when alignment into the opposite direction is true (as the dot product is 0 there as well, and that's the opposite of our requested alignment)
                //TODO: Allow this to be maximally quick by giving the option to input ship mass (with gyro rotation force and ship mass we know exactly when to decelerate, reaching maximum speeds at any time)
                //i.e. maybe by using a previously determined and stored in this class weight coefficient that multiplies these numbers here
                //As this method is designed to be run on UpdateFrequency.Update1, it is optimized for performance. Maybe the float instantiation could be moved however.
                //Aligning to a target with a certain vector requires the two rotation axes used to align need to be in perpendicular position to the target, i.e. a 0 dot product
                float rotationRemainder0 = (float)anchorToAlignWorldMatrix.GetDirectionVector(rotationHelper.DotProductFactorDirections[0]).Dot(target) * 10;
                float rotationRemainder1 = (float)anchorToAlignWorldMatrix.GetDirectionVector(rotationHelper.DotProductFactorDirections[1]).Dot(target) * 10;
                rotationRemainder0 = rotationRemainder0 > speedLimitInRadPS ? speedLimitInRadPS : rotationRemainder0 < -speedLimitInRadPS ? -speedLimitInRadPS : rotationRemainder0;
                rotationRemainder1 = rotationRemainder1 > speedLimitInRadPS ? speedLimitInRadPS : rotationRemainder1 < -speedLimitInRadPS ? -speedLimitInRadPS : rotationRemainder1;
                for(int i = 0; i < gyroList.Count; i++) {
                    gyroList[i].SetRotation(rotationHelper.RotationAxes[0], rotationRemainder0);
                    gyroList[i].SetRotation(rotationHelper.RotationAxes[1], rotationRemainder1);
                }
                return Math.Abs(rotationRemainder0) < alignmentSuccessThreshold && Math.Abs(rotationRemainder1) < alignmentSuccessThreshold;
            }
            protected void Enable(bool powerToFull = false) {
                float gyroPower = powerToFull || gyroBlock.GyroPower == 0 ? 1 : gyroBlock.GyroPower;
                gyroBlock.Enabled = true;
                gyroBlock.GyroOverride = true;
                gyroBlock.GyroPower = gyroPower;
            }
            protected void SetRotation(MyTuple<PrincipalAxis, int> rotationAxis, float radPerSecond) {
                Enable(true);
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
            public void StopRotation(bool leaveOverrideOn = false) {
                Enable();
                gyroBlock.Pitch = 0;
                gyroBlock.Yaw = 0;
                gyroBlock.Roll = 0;
                gyroBlock.GyroOverride = leaveOverrideOn;
            }
        }
        public sealed class SunOrbit {
            //Contains the minimum 3 data points to describe a sun orbit, sufficient enough to track it with no more input
            private Vector3D _planeNormal = Vector3D.Zero;
            private float _angularSpeed = 0; //in Radians per second, minimum daytime is 1 minute
            private int _rotationDirection = 0; //1 is clockwise, -1 is counter clockwise, rotation direction around planeNormal (right hand rule, our Down aligned with planeNormal)
            public enum DataPoint { PlaneNormal = 1, Direction, AngularSpeed }
            public Vector3D PlaneNormal {
                get { return _planeNormal; }
                set { _planeNormal = Vector3D.Normalize(value); }
            }
            public float AngularSpeedRadPS {
                get { return _angularSpeed; }
                set { _angularSpeed = value >= MINIMUM_ANGULAR_SPEED ? value : 0; }
            }
            public int Direction {
                get { return _rotationDirection; }
                set { _rotationDirection = value == 1 || value == -1 ? value : 0; }
            }
            public float PrecisionThreshold { get; private set; } //ranges from 0 to 1, 1 being perfect precision and 0 = every possible value is considered aligned to the sun
            private static double MINIMUM_ANGULAR_SPEED = 1d / 60 * 2 * Math.PI;
            private const string INI_SECTION_NAME = "Sun Orbit";
            private const string INI_PLANE_NORMAL_KEY = "Plane normal";
            private const string INI_ANGULAR_SPEED_KEY = "Orbital speed";
            private const string INI_DIRECTION_KEY = "Orbit direction";
            public SunOrbit(float precisionThreshold, MyIni storageIni = null) {
                if(storageIni is object) ReadFromIni(storageIni);
                PrecisionThreshold = precisionThreshold;
                PlaneNormal = DEFAULT_PLANE_NORMAL;
            }
            public bool IsMapped(DataPoint dataPoint = 0) {
                switch(dataPoint) {
                    case DataPoint.PlaneNormal:
                        return !_planeNormal.IsZero();
                    case DataPoint.Direction:
                        return _rotationDirection != 0;
                    case DataPoint.AngularSpeed:
                        return _angularSpeed != 0;
                    default:
                        return !_planeNormal.IsZero() && _rotationDirection != 0 && _angularSpeed != 0;
                }
            }
            public void ClearData(DataPoint dataPoint = 0) {
                switch(dataPoint) {
                    case DataPoint.PlaneNormal:
                        _planeNormal = Vector3D.Zero;
                        break;
                    case DataPoint.AngularSpeed:
                        _angularSpeed = 0;
                        break;
                    case DataPoint.Direction:
                        _rotationDirection = 0;
                        break;
                    default:
                        _planeNormal = Vector3D.Zero;
                        _rotationDirection = 0;
                        _angularSpeed = 0;
                        break;
                }
            }
            public void WriteToIni(MyIni storageIni) {
                storageIni.Set(INI_SECTION_NAME, INI_PLANE_NORMAL_KEY, _planeNormal.ToString());
                storageIni.Set(INI_SECTION_NAME, INI_DIRECTION_KEY, _rotationDirection);
                storageIni.Set(INI_SECTION_NAME, INI_ANGULAR_SPEED_KEY, _angularSpeed);
            }
            public void ReadFromIni(MyIni storageIni) {
                if(storageIni.ContainsSection(INI_SECTION_NAME)) {
                    Vector3D.TryParse(storageIni.Get(INI_SECTION_NAME, INI_PLANE_NORMAL_KEY).ToString(), out _planeNormal);
                    _rotationDirection = storageIni.Get(INI_SECTION_NAME, INI_DIRECTION_KEY).ToInt32();
                    _angularSpeed = storageIni.Get(INI_SECTION_NAME, INI_ANGULAR_SPEED_KEY).ToSingle();
                }
            }
            public string PrintableDataPoints() {
                return $"{INI_PLANE_NORMAL_KEY} = {PlaneNormal}\n" + $"{INI_DIRECTION_KEY} = {Direction}\n" + $"{INI_ANGULAR_SPEED_KEY} = {AngularSpeedRadPS}\n";
            }
            public float DaytimeInMinutes() {
                return IsMapped(DataPoint.AngularSpeed) ? (float)(2 * Math.PI / (_angularSpeed / (100f / 60)) / 60) : float.NaN;
            }
        }
        public Program() {
            InitializeBlocks();
            //TODO: Overhaul init once feature complete (only assign here via ini.Get, otherwise use the defaults also defined here
            if(storageIni.TryParse(Storage)) {
                currentRoutine = (Routines)storageIni.Get("storage", "currentRoutine").ToInt32(); //TODO: Leave ROutine at None and do not save it. Instead, use NextRoutineToMapSunOrbit()
                routineToResumeOn = (Routines)storageIni.Get("storage", "routineToResumeOn").ToInt32();
                Vector3D.TryParse(storageIni.Get("storage", "sunOrbitPointOne").ToString(Vector3D.Zero.ToString()), out sunOrbitPointOne);
                sunOrbit.ReadFromIni(storageIni);
            }
            lcdTextDefaultLength = lcdText.Length;
            lcd.ContentType = ContentType.TEXT_AND_IMAGE;

            dicRoutines = new Dictionary<Routines, Action>() {
                {Routines.None, () => { } },
                {Routines.Pause, () => {
                    if(currentRoutineRepeats == targetRoutineRepeats) ChangeCurrentRoutine(routineToResumeOn);
                    else currentRoutineRepeats++;
                } },
                {Routines.DetermineSunPlaneNormal, () => DetermineSunPlaneNormal()},
                {Routines.DetermineSunPlaneNormalManual, () => DetermineSunPlaneNormalManual(lcd)},
                {Routines.AlignPanelDownToSunPlaneNormal, () => {
                    if(Gyroscope.AlignToTargetNormalizedVector(sunOrbit.PlaneNormal, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros))
                        ChangeCurrentRoutine(Routines.AlignPanelToSun);
                }},
                {Routines.AlignPanelToSun, () => {
                    Gyroscope.AlignToTargetNormalizedVector(targetVector, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros);
                    if((currentUpdateSource & UpdateType.Update100) != 0) {
                        UpdateExposureValues();
                        if (mostRecentSunExposure > EXPOSURE_OFFSET_THRESHOLD) {ChangeCurrentRoutine(routineToResumeOn); return; }
                        else if (exposureDelta < 0) targetVector = rotationHelper.RotatedVectorCounterClockwise;
                        if (rotationHelper.IsAlignedWithNormalizedTargetVector(targetVector, referenceSolarPanel.WorldMatrix.Forward)) {
                            ChangeCurrentRoutine(Routines.AlignPanelDownToSunPlaneNormal);
                        }
                    }
                }},
                //TEST
                {Routines.ObtainPreliminaryAngularSpeed, () => {
                    UpdateExposureValues();
                    if (IsAlignedToPlaneNormalOrWithinThresholdExposure()){
                        angleDeltaMeasurements[currentRoutineRepeats] = (float)Math.Abs(Math.Acos(mostRecentSunExposure)
                            - Math.Acos(previousSunExposure));
                        if (currentRoutineRepeats >= angleDeltaMeasurements.Length) {
                            preliminaryAngularSpeed = angleDeltaMeasurements.Average();
                            ChangeCurrentRoutine(Routines.DetermineSunOrbitAngularSpeed);
                        }
                        else currentRoutineRepeats++;
                    }
                }},
                //TODO: Rework this garbage. Align towards the sun using a quaternion (to know whether we're not obstructed), then align 15 degrees in either direction.
                //If exposure keeps increasing the sun is moving in the direction that we rotated around, otherwise opposite. Snappier and easier!
                {Routines.PrepareDetermineSunOrbitDirection, () => {
                    UpdateExposureValues();
                    gyroSpeed = Math.Max(0.008f, Math.Abs(mostRecentSunExposure - 0.5f)*0.1f);
                    foreach (Gyroscope gyro in registeredGyros) gyro.SetRotation(PrincipalAxis.Yaw, gyroSpeed);
                    if (mostRecentSunExposure >= 0.45f && mostRecentSunExposure <= 0.55f) ChangeCurrentRoutine(Routines.DetermineSunOrbitDirection);
                }},
                //{Routines.DetermineSunOrbitDirection, () => DetermineSunOrbitDirection()},
                {Routines.DetermineSunOrbitDirection, () => {
                    bool isAligned = Gyroscope.AlignToTargetNormalizedVector(rotationHelper.RotatedVectorClockwise, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros);
                    if (isAligned) {
                        Runtime.UpdateFrequency = UpdateFrequency.Update100;
                        if((currentUpdateSource & UpdateType.Update100) != 0) {
                            UpdateExposureValues();
                            exposureDeltaMeasurements[currentRoutineRepeats] = exposureDelta;
                            currentRoutineRepeats++;
                            if (currentRoutineRepeats >= 3) {
                                sunOrbit.Direction = Math.Sign(exposureDeltaMeasurements.Average());
                                ChangeCurrentRoutine(NextRoutineToMapSunOrbit()); 
                            }
                    } }
                }},
                {Routines.Debug, () => { UpdateExposureValues(); lcd.WriteText($"exposureDelta: {exposureDelta}\nmostRecentExposure: {mostRecentSunExposure}\npreviousExposure:{previousSunExposure}"); } },
            };
            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                {"Run", () => {if (!sunOrbit.IsMapped()) ChangeCurrentRoutine(NextRoutineToMapSunOrbit()); } },
                {"Halt", () => ChangeCurrentRoutine(Routines.None) },
                {"Reinitialize", () => InitializeBlocks() },
                {"ClearData", () => {
                    SunOrbit.DataPoint dataPoint;
                    Enum.TryParse(_commandLine.Argument(1), out dataPoint);
                    sunOrbit.ClearData(dataPoint);
                } },
                {"Debug", () => Me.CustomData = sunOrbit.PrintableDataPoints() + sunOrbit.DaytimeInMinutes().ToString() },
                {"AlignToSun", () => {routineToResumeOn = Routines.None; ChangeCurrentRoutine(Routines.AlignPanelDownToSunPlaneNormal); } },
                {"SunSpeed", () => ChangeCurrentRoutine(Routines.Debug) },
            };
        }
        public void Save() {
            storageIni.Clear();
            storageIni.Set("storage", "currentRoutine", (int)currentRoutine);
            storageIni.Set("storage", "routineToResumeOn", (int)currentRoutine);
            storageIni.Set("storage", "sunPlaneOrbitPointOne", sunOrbitPointOne.ToString());
            //TODO: rotationHelper rotation preparation needs to be stored in case we save during an alignment process
            //Alternatively, routines are discarded during these processes (as our program flow might be robust enough), i.e. let NextRoutineToMapSunOrbit() initiate it properly again
            //TODO: Store current routine repeats as well for pause
            sunOrbit.WriteToIni(storageIni);
            Storage = storageIni.ToString();
        }
        public void Main(string argument, UpdateType updateSource) {
            Echo(Runtime.LastRunTimeMs.ToString());
            Echo(Runtime.UpdateFrequency.ToString());
            Echo(currentRoutine.ToString() + "\n");
            currentUpdateSource = updateSource;
            if((updateSource & AUTOMATIC_UPDATE_TYPE) == 0) {
                if(_commandLine.TryParse(argument)) {
                    Action currentCommand;
                    if(dicCommands.TryGetValue(_commandLine.Argument(0), out currentCommand)) currentCommand();
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
        public void ChangeCurrentRoutine(Routines targetRoutine) {
            UpdateFrequency updateFrequency = UpdateFrequency.Update100;
            currentStage = 0;
            currentRoutineRepeats = 0;
            for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].StopRotation(true);
            switch(targetRoutine) {
                case Routines.None:
                    updateFrequency = UpdateFrequency.None;
                    break;
                case Routines.Pause:
                    routineToResumeOn = currentRoutine;
                    break;
                case Routines.DetermineSunPlaneNormalManual:
                    for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].gyroBlock.GyroOverride = false;
                    break;
                case Routines.DetermineSunPlaneNormal:
                    updateFrequency = UpdateFrequency.Update10 | UpdateFrequency.Update100;
                    break;
                case Routines.AlignPanelDownToSunPlaneNormal:
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Down);
                    updateFrequency = UpdateFrequency.Update1;
                    break;
                case Routines.AlignPanelToSun:
                    UpdateExposureValues();
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Forward);
                    rotationHelper.GenerateRotatedNormalizedVectorsAroundAxisByAngle(referenceSolarPanel.WorldMatrix.Forward,
                        referenceSolarPanel.WorldMatrix.Down, Math.Acos(mostRecentSunExposure));
                    targetVector = rotationHelper.RotatedVectorClockwise;
                    updateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;
                    break;
                case Routines.DetermineSunOrbitDirection:
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Forward);
                    rotationHelper.GenerateRotatedNormalizedVectorsAroundAxisByAngle(referenceSolarPanel.WorldMatrix.Forward,
                        referenceSolarPanel.WorldMatrix.Down, 0.4f);
                    break;
                case Routines.DetermineSunOrbitAngularSpeed:
                    if(targetDigit == 0) {
                        char[] valueChars = preliminaryAngularSpeed.ToString().Substring(2).ToCharArray(); //First two characters are 0 and ., so they are irrelevant.
                        for(int i = 0; i < valueChars.Length; i++) if(valueChars[i] != '0') targetDigit = i;
                    }
                    break;
                case Routines.Debug:
                    float radPS = 1f / (252.229f * 60) * 2 * (float)Math.PI;
                    foreach(var gyro in registeredGyros) gyro.SetRotation(PrincipalAxis.Yaw, radPS);
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
        public bool IsAlignedToPlaneNormalOrWithinThresholdExposure() {
            if(!rotationHelper.IsAlignedWithNormalizedTargetVector(sunOrbit.PlaneNormal, referenceSolarPanel.WorldMatrix.Down) || mostRecentSunExposure < EXPOSURE_OFFSET_THRESHOLD) {
                routineToResumeOn = currentRoutine;
                ChangeCurrentRoutine(Routines.AlignPanelDownToSunPlaneNormal);
                return false;
            }
            else return true;
        }
        public Routines NextRoutineToMapSunOrbit() {
            Routines returnRoutine;
            if(!sunOrbit.IsMapped(SunOrbit.DataPoint.PlaneNormal)) {
                if(determineOrbitPlaneNormalManually) returnRoutine = Routines.DetermineSunPlaneNormalManual;
                else returnRoutine = Routines.DetermineSunPlaneNormal;
            }
            else if(!sunOrbit.IsMapped(SunOrbit.DataPoint.Direction)) {
                returnRoutine = Routines.AlignPanelDownToSunPlaneNormal;
                routineToResumeOn = Routines.DetermineSunOrbitDirection;
            }
            else if(!sunOrbit.IsMapped(SunOrbit.DataPoint.AngularSpeed)) {
                returnRoutine = Routines.AlignPanelDownToSunPlaneNormal;
                routineToResumeOn = Routines.DetermineSunOrbitAngularSpeed;
            }
            else {
                returnRoutine = Routines.None;
                routineToResumeOn = Routines.None;
            }
            return returnRoutine;
        }
        #region Sun orbit data point determination functions
        public void DetermineSunPlaneNormalManual(IMyTextSurface lcd) {
            UpdateExposureValues();
            if(lcdText.Length > lcdTextDefaultLength) lcdText.Remove(lcdTextDefaultLength + 1, lcdText.Length - lcdTextDefaultLength - 1);
            //TEST: Verify whether the rounding is accurate for non-small vanilla panels
            lcdText.Append(Math.Round(mostRecentSunExposure, 8));
            if(mostRecentSunExposure >= PRECISION_THRESHOLD) {
                if(sunOrbitPointOne.IsZero()) {
                    sunOrbitPointOne = referenceSolarPanel.WorldMatrix.Forward;
                    lcd.WriteText($"Exposure value {mostRecentSunExposure} has been stored,\nas it met the precision threshold of {PRECISION_THRESHOLD}.\n" +
                        $"Please wait about five seconds before resuming to align a second time.");
                    targetRoutineRepeats = 3;
                    ChangeCurrentRoutine(Routines.Pause);
                }
                else {
                    sunOrbit.PlaneNormal = sunOrbitPointOne.Cross(referenceSolarPanel.WorldMatrix.Forward);
                    lcd.WriteText($"Success!\nA second exposure value of {mostRecentSunExposure} has been stored.\nProceeding automatically...");
                    sunOrbitPointOne = Vector3D.Zero;
                    ChangeCurrentRoutine(NextRoutineToMapSunOrbit());
                }
                return;
            }
            lcd.WriteText(lcdText);
        }
        public void DetermineSunPlaneNormal() {
            //Two points are determined where sunExposure >= precision threshold
            UpdateExposureValues();
            if(mostRecentSunExposure >= PRECISION_THRESHOLD) {
                if(sunOrbitPointOne.IsZero()) {
                    sunOrbitPointOne = referenceSolarPanel.WorldMatrix.Forward;
                    ChangeCurrentRoutine(Routines.Pause);
                }
                else {
                    sunOrbit.PlaneNormal = sunOrbitPointOne.Cross(referenceSolarPanel.WorldMatrix.Forward);
                    sunOrbitPointOne = Vector3D.Zero;
                    ChangeCurrentRoutine(NextRoutineToMapSunOrbit());
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
            //Rotation direction will be:
            //+, +: -1     +, -: 1      -, +: 1     -, -: -1
            UpdateExposureValues();
            if(referenceSolarPanel.WorldMatrix.Down.Dot(sunOrbit.PlaneNormal) < 0.999f) ChangeCurrentRoutine(Routines.AlignPanelDownToSunPlaneNormal);
            if(mostRecentSunExposure >= 0.3f && mostRecentSunExposure <= 0.7f) {
                bool previousMeasure;
                if(exposureDelta == 0) return;
                switch(currentStage) {
                    case 0:
                        previousMeasure = exposureIncreasedDuringIdle;
                        exposureIncreasedDuringIdle = exposureDelta > 0;
                        if(exposureIncreasedDuringIdle != previousMeasure) currentRoutineRepeats = 0;
                        if(currentRoutineRepeats >= 3) {
                            foreach(Gyroscope gyro in registeredGyros) gyro.SetRotation(PrincipalAxis.Yaw, 0.033f);
                            currentStage++;
                            currentRoutineRepeats = 0;
                        }
                        break;
                    case 1:
                        previousMeasure = exposureIncreasedDuringRotation;
                        exposureIncreasedDuringRotation = exposureDelta > 0;
                        if(exposureIncreasedDuringRotation != previousMeasure) currentRoutineRepeats = 0;
                        if(currentRoutineRepeats >= 3) {
                            sunOrbit.Direction = exposureIncreasedDuringIdle ^ exposureIncreasedDuringRotation ? 1 : -1;
                            ChangeCurrentRoutine(NextRoutineToMapSunOrbit());
                        }
                        break;
                }
                currentRoutineRepeats++;
            }
            else ChangeCurrentRoutine(Routines.PrepareDetermineSunOrbitDirection);
        }
        public void DetermineSunAngularSpeed() {

        }
        #endregion
        #region Initialization functions
        public void InitializeBlocks() {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType(blocks, block => block.IsSameConstructAs(Me) && block.IsFunctional);
            referenceSolarPanel = GetBlock<IMySolarPanel>(NAME_REFERENCE_SOLAR_PANEL, blocks);
            maxPossibleOutput = referenceSolarPanel.CubeGrid.GridSizeEnum == MyCubeSize.Small ? 0.04f : 0.16f; //in MW
            lcd = GetBlock<IMyTextPanel>(NAME_LCD, blocks);

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
        #endregion
        #endregion
    }
}
