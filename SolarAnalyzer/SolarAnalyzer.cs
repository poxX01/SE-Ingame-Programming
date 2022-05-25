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
        //Leave empty to have the program use the first functional one found on the same construct
        const string NAME_REFERENCE_SOLAR_PANEL = "";
        const string NAME_LCD = "";
        const string INI_SECTION_NAME = "SolarAnalyzer";
        const string INI_KEY_CURRENT_ROUTINE = "currentRoutine";
        const string INI_KEY_ROUTINE_TO_RESUME_ON = "routineToResumeOn";
        const string INI_KEY_BUFFER_VECTOR = "bufferVector";
        const string INI_KEY_FLOAT_LIST_AVERAGE = "floatList.Average()";
        const string INI_KEY_FLOAT_LIST_COUNT = "floatList.Count";

        //TODO: LOW PRIORITY: Support a large panel and modded ones (ratios may be different, especially with rounding throughout code)
        //E.g. introduce (in custom data) an entry for panel max output, however we'd need to check the rounding done in DetermineSunPlaneNormalManual
        //const float PRECISION_THRESHOLD = 0.999985f; //150W off perfect readout precision in a small panel
        const float PRECISION_THRESHOLD = 1;
        const float ALIGN_TO_SUN_OFFSET_THRESHOLD = PRECISION_THRESHOLD - 0.001f; //Any exposure value between 1 and this is considered aligned for that routine.
        const int MEASUREMENTS_TARGET_AMOUNT_SUN_DIRECTION = 8;
        const int MEASUREMENTS_TARGET_AMOUNT_SUN_SPEED = 2500;
        const UpdateType AUTOMATIC_UPDATE_TYPE = UpdateType.Update100 | UpdateType.Update10 | UpdateType.Update1 | UpdateType.Once;
        float maxPossibleOutput; //in MW
        bool determineOrbitPlaneNormalManually = true;

        int currentRoutineRepeats;
        int targetRoutineRepeats; //for Pause()
        float previousSunExposure;
        float mostRecentSunExposure; //1 for perfect exposure, 0 for absolutely no sun on our reference panel
        float exposureDelta;

        //Storage for DetermineSunPlaneNormal()
        const float SUN_EXPOSURE_PAUSE_THRESHOLD = -0.2f; //if exposure delta ever goes below this value in one run, Pause() is called
        PrincipalAxis currentRotationAxis = PrincipalAxis.Pitch;
        bool haveNotInvertedYetThisAxis = true;
        bool exposureDeltaGreaterThanZeroOnLastRun = false;
        int[] currentSigns = { 1, 1 };

        Vector3D bufferVector = new Vector3D(); //used in AlignForwardToSun() and DetermineSunPlaneNormals
        List<float> floatList = new List<float>(MEASUREMENTS_TARGET_AMOUNT_SUN_SPEED); //for measurements in ObtainPreliminaryAngularSpeed and DetermineSunOrbitDirection
        List<float> exposureDeltasList = new List<float>(); //TODO: Remove once Routines.Debug gets removed!

        //Storage for MANUAL LCD feedback
        StringBuilder lcdText = new StringBuilder($"Align towards the sun within 1 and target margin.\n\nTarget: {PRECISION_THRESHOLD}\nCurrent: ");
        readonly int lcdTextDefaultLength;
        IMyTextPanel lcd;

        IMySolarPanel referenceSolarPanel;
        List<Gyroscope> registeredGyros = new List<Gyroscope>();
        RotationHelper rotationHelper = new RotationHelper();
        SunOrbit sunOrbit;

        UpdateType currentUpdateSource;
        Routine currentRoutine;
        Routine routineToResumeOn; //Will resume on this routime after an intermediate routine (e.g. Pause and after AlignmentRoutines)
        MyIni storageIni = new MyIni();
        MyCommandLine _commandLine = new MyCommandLine();
        Dictionary<Routine, Action> dicRoutines;
        Dictionary<string, Action> dicCommands;
        public enum Routine {
            None,
            Pause,
            DetermineSunPlaneNormal,
            DetermineSunPlaneNormalManual,
            AlignPanelDownToSunPlaneNormal,
            AlignPanelToSun,
            DetermineSunOrbitDirection,
            DetermineSunAngularSpeed,
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
            private const double MAXIMUM_ANGULAR_SPEED = 1d / 60 * 2 * Math.PI;
            private const double MINIMUM_ANGULAR_SPEED = 1440d / 60 * 2 * Math.PI;
            private const string INI_SECTION_NAME = "Sun Orbit";
            private const string INI_KEY_PLANE_NORMAL = "Plane normal";
            private const string INI_KEY_ANGULAR_SPEED = "Orbital speed";
            private const string INI_KEY_DIRECTION = "Orbit direction";
            private const string IGC_BROADCAST_REQUEST_DATA_TAG = "Sun Orbit Broadcast: Request data";
            private const string IGC_BROADCAST_OVERRIDE_DATA_TAG = "Sun Orbit Broadcast: Override data";
            private const string IGC_UNICAST_TAG = "Sun Orbit Unicast";
            private readonly IMyIntergridCommunicationSystem IGC;
            private readonly IMyBroadcastListener broadcastRequestDataListener;
            private readonly IMyBroadcastListener broadcastOverrideDataListener;
            private readonly bool isSolarAnalyzer;
            private void OverrideDataFromMessage(MyIGCMessage message) {
                MyTuple<Vector3D, int, float> data = (MyTuple<Vector3D, int, float>)message.Data;
                PlaneNormal = data.Item1;
                Direction = data.Item2;
                AngularSpeedRadPS = data.Item3;
            }

            private Vector3D _planeNormal = Vector3D.Zero;
            private int _rotationDirection = 0; //1 is clockwise, -1 is counter clockwise, Down aligned with planeNormal (right hand rule, fingers pointing in clockwise direction)
            private float _angularSpeed = 0; //in Radians per second
            public enum DataPoint { PlaneNormal = 1, Direction, AngularSpeed }
            public Vector3D PlaneNormal {
                get { return _planeNormal; }
                set { _planeNormal = value == Vector3D.Zero ? Vector3D.Normalize(value) : _planeNormal; }
            }
            public int Direction {
                get { return _rotationDirection; }
                set { _rotationDirection = value == 1 || value == -1 ? value : _rotationDirection; }
            }
            public float AngularSpeedRadPS {
                get { return _angularSpeed; }
                set { _angularSpeed = value <= MAXIMUM_ANGULAR_SPEED && value > 0 ? value : _angularSpeed; }
            }
            public SunOrbit(IMyIntergridCommunicationSystem IGC, bool isSolarAnalyzer, bool fillWithMockData = false) {
                this.isSolarAnalyzer = isSolarAnalyzer;
                this.IGC = IGC;
                IGC.UnicastListener.SetMessageCallback();
                broadcastRequestDataListener = IGC.RegisterBroadcastListener(IGC_BROADCAST_REQUEST_DATA_TAG);
                broadcastRequestDataListener.SetMessageCallback(IGC_BROADCAST_REQUEST_DATA_TAG);
                if(!isSolarAnalyzer) {
                    broadcastOverrideDataListener = IGC.RegisterBroadcastListener(IGC_BROADCAST_OVERRIDE_DATA_TAG);
                    broadcastOverrideDataListener.SetMessageCallback(IGC_BROADCAST_OVERRIDE_DATA_TAG);
                }
                if(fillWithMockData) { PlaneNormal = new Vector3D(0, 1, 0); Direction = 1; AngularSpeedRadPS = 0.001f; }
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
            public void WriteToIni(MyIni ini) {
                ini.Set(INI_SECTION_NAME, INI_KEY_PLANE_NORMAL, _planeNormal.ToString());
                ini.Set(INI_SECTION_NAME, INI_KEY_DIRECTION, _rotationDirection);
                ini.Set(INI_SECTION_NAME, INI_KEY_ANGULAR_SPEED, _angularSpeed);
            }
            public void ReadFromIni(MyIni ini) {
                if(ini.ContainsSection(INI_SECTION_NAME)) {
                    Vector3D.TryParse(ini.Get(INI_SECTION_NAME, INI_KEY_PLANE_NORMAL).ToString(), out _planeNormal);
                    _rotationDirection = ini.Get(INI_SECTION_NAME, INI_KEY_DIRECTION).ToInt32();
                    _angularSpeed = ini.Get(INI_SECTION_NAME, INI_KEY_ANGULAR_SPEED).ToSingle();
                }
            }
            public void IGC_ProcessMessages() {
                if(IsMapped(DataPoint.PlaneNormal)) {
                    while(broadcastRequestDataListener.HasPendingMessage) {
                        MyIGCMessage requestDataMessage = broadcastRequestDataListener.AcceptMessage();
                        if (!((bool)requestDataMessage.Data && !isSolarAnalyzer))
                            IGC.SendUnicastMessage(requestDataMessage.Source, IGC_UNICAST_TAG, new MyTuple<Vector3D, int, float>(PlaneNormal, Direction, AngularSpeedRadPS));
                    }
                }
                if(!isSolarAnalyzer) { while(broadcastOverrideDataListener.HasPendingMessage) OverrideDataFromMessage(broadcastRequestDataListener.AcceptMessage()); }
                while(IGC.UnicastListener.HasPendingMessage) {
                    MyIGCMessage unicastMessage = IGC.UnicastListener.AcceptMessage();
                    if(unicastMessage.Tag == IGC_UNICAST_TAG) OverrideDataFromMessage(unicastMessage);
                }
            }
            public void IGC_BroadcastSendOverrideData() {
                if(IsMapped()) IGC.SendBroadcastMessage(IGC_BROADCAST_OVERRIDE_DATA_TAG, new MyTuple<Vector3D, int, float>(PlaneNormal, Direction, AngularSpeedRadPS));
            }
            public void IGC_BroadcastRequestData(bool requestOnlyFromAnalyzers) {
                if(!isSolarAnalyzer) IGC.SendBroadcastMessage(IGC_BROADCAST_REQUEST_DATA_TAG, requestOnlyFromAnalyzers);
            }
            public string PrintableDataPoints() {
                return $"{INI_KEY_PLANE_NORMAL} = {PlaneNormal}\n" +
                    $"{INI_KEY_DIRECTION} = {Direction}\n" +
                    $"{INI_KEY_ANGULAR_SPEED} = {AngularSpeedRadPS}\n";
            }
            public float DaytimeInMinutes() {
                return IsMapped(DataPoint.AngularSpeed) ? (float)(2 * Math.PI / AngularSpeedRadPS / 60) : float.NaN;
            }
        }
        public Program() {
            InitializeBlocks();
            sunOrbit = new SunOrbit(IGC, true);
            #region ReadFromIni
            storageIni.TryParse(Storage);
            currentRoutine = (Routine)storageIni.Get(INI_SECTION_NAME, INI_KEY_CURRENT_ROUTINE).ToInt32();
            routineToResumeOn = (Routine)storageIni.Get(INI_SECTION_NAME, INI_KEY_ROUTINE_TO_RESUME_ON).ToInt32();
            Vector3D.TryParse(storageIni.Get(INI_SECTION_NAME, INI_KEY_BUFFER_VECTOR).ToString(Vector3D.Zero.ToString()), out bufferVector);
            if(currentRoutine == Routine.DetermineSunAngularSpeed) {
                float storedFloatListAverage = storageIni.Get(INI_SECTION_NAME, INI_KEY_FLOAT_LIST_AVERAGE).ToSingle();
                int storedFloatListCount = storageIni.Get(INI_SECTION_NAME, INI_KEY_FLOAT_LIST_COUNT).ToInt32();
                for(int i = 0; i < storedFloatListCount; i++) floatList.Add(storedFloatListAverage);
            }
            sunOrbit.ReadFromIni(storageIni);
            #endregion
            #region Dictionary routines
            dicRoutines = new Dictionary<Routine, Action>() {
                {Routine.None, () => { } },
                {Routine.Pause, () => {
                    if(currentRoutineRepeats == targetRoutineRepeats) ChangeCurrentRoutine(routineToResumeOn);
                    else currentRoutineRepeats++;
                } },
                {Routine.DetermineSunPlaneNormal, () => DetermineSunPlaneNormal()},
                {Routine.DetermineSunPlaneNormalManual, () => DetermineSunPlaneNormalManual(lcd)},
                {Routine.AlignPanelDownToSunPlaneNormal, () => {
                    if(Gyroscope.AlignToTargetNormalizedVector(sunOrbit.PlaneNormal, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros) &&
                    (currentUpdateSource & UpdateType.Update100) != 0)
                        ChangeCurrentRoutine(Routine.AlignPanelToSun);
                }},
                {Routine.AlignPanelToSun, () => {
                    Gyroscope.AlignToTargetNormalizedVector(bufferVector, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros);
                    if((currentUpdateSource & UpdateType.Update100) != 0) {
                        UpdateExposureValues();
                        if (mostRecentSunExposure > ALIGN_TO_SUN_OFFSET_THRESHOLD) {ChangeCurrentRoutine(routineToResumeOn); return; }
                        else if (exposureDelta < 0) bufferVector = rotationHelper.RotatedVectorCounterClockwise;
                        if (rotationHelper.IsAlignedWithNormalizedTargetVector(bufferVector, referenceSolarPanel.WorldMatrix.Forward)) {
                            ChangeCurrentRoutine(Routine.AlignPanelDownToSunPlaneNormal);
                        }
                    }
                }},
                {Routine.DetermineSunOrbitDirection, () => DetermineSunOrbitDirection() },
                {Routine.DetermineSunAngularSpeed, () => DetermineSunAngularSpeed()},
                {Routine.Debug, () => {
                    UpdateExposureValues();
                    if (currentRoutineRepeats > 0) exposureDeltasList.Add(exposureDelta);
                    currentRoutineRepeats++;
                    lcd.WriteText($"mostRecentExposure: {mostRecentSunExposure}\npreviousExposure: {previousSunExposure}\nexposureDelta: {exposureDelta}\n" +
                        $"preliminaryAngularSpeed: {sunOrbit.AngularSpeedRadPS}\nexposureDeltaAveragesCount: {exposureDeltasList.Count}\n" +
                        $"exposureDeltaAverage: {(exposureDeltasList.Count > 0 ? exposureDeltasList.Average() : 0)}\n");
                }},
            };
            #endregion
            #region Dictionary commands
            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                {"Run", () => {if (!sunOrbit.IsMapped()) ChangeCurrentRoutine(NextRoutineToMapSunOrbit()); } },
                {"Halt", () => ChangeCurrentRoutine(Routine.None) },
                {"Reinitialize", () => InitializeBlocks() },
                {"ClearData", () => {
                    SunOrbit.DataPoint dataPoint;
                    Enum.TryParse(_commandLine.Argument(1), out dataPoint);
                    sunOrbit.ClearData(dataPoint);
                } },
                {"PrintData", () => Me.CustomData = sunOrbit.PrintableDataPoints() },
                {"AlignToSun", () => {routineToResumeOn = Routine.None; ChangeCurrentRoutine(Routine.AlignPanelDownToSunPlaneNormal); } },
                {"Debug", () => {
                    routineToResumeOn = Routine.Debug;
                    ChangeCurrentRoutine(Routine.AlignPanelDownToSunPlaneNormal);
                } },
            };
            #endregion
            lcdTextDefaultLength = lcdText.Length;
            lcd.ContentType = ContentType.TEXT_AND_IMAGE;
            ChangeCurrentRoutine(currentRoutine);
        }
        public void Save() {
            storageIni.Clear();
            storageIni.Set(INI_SECTION_NAME, INI_KEY_CURRENT_ROUTINE, (int)currentRoutine);
            storageIni.Set(INI_SECTION_NAME, INI_KEY_ROUTINE_TO_RESUME_ON, (int)currentRoutine);
            storageIni.Set(INI_SECTION_NAME, INI_KEY_BUFFER_VECTOR, bufferVector.ToString());
            if(currentRoutine == Routine.DetermineSunAngularSpeed) {
                storageIni.Set(INI_SECTION_NAME, INI_KEY_FLOAT_LIST_AVERAGE, floatList.Average());
                storageIni.Set(INI_SECTION_NAME, INI_KEY_FLOAT_LIST_COUNT, floatList.Count);
            }
            sunOrbit.WriteToIni(storageIni);
            Storage = storageIni.ToString();
        }
        public void Main(string argument, UpdateType updateSource) {
            Echo(Runtime.LastRunTimeMs.ToString());
            Echo(Runtime.UpdateFrequency.ToString());
            Echo(currentRoutine.ToString() + "\n");
            currentUpdateSource = updateSource;
            if((updateSource & (UpdateType.Trigger | UpdateType.Terminal)) != 0) {
                if(_commandLine.TryParse(argument)) {
                    Action currentCommand;
                    if(dicCommands.TryGetValue(_commandLine.Argument(0), out currentCommand)) currentCommand();
                    else {
                        StringBuilder printable = new StringBuilder("An invalid command was passed as an argument. Valid arguments are:\n");
                        foreach(string key in dicCommands.Keys) {
                            if(key == "Run") printable.AppendLine(key + " (default, if no command specified)");
                            else printable.AppendLine(key);
                        }
                        Echo(printable.ToString());
                    }
                }
                else dicCommands["Run"]();
            }
            else if((updateSource & UpdateType.IGC) != 0) sunOrbit.IGC_ProcessMessages();
            else dicRoutines[currentRoutine]();
        }
        public void ChangeCurrentRoutine(Routine targetRoutine) {
            UpdateFrequency updateFrequency = UpdateFrequency.Update100;
            currentRoutineRepeats = 0;
            for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].StopRotation(true);
            switch(targetRoutine) {
                case Routine.None:
                    for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].gyroBlock.GyroOverride = false;
                    updateFrequency = UpdateFrequency.None;
                    break;
                case Routine.Pause:
                    if(routineToResumeOn == Routine.DetermineSunPlaneNormalManual) targetRoutineRepeats = 3;
                    break;
                case Routine.DetermineSunPlaneNormalManual:
                    for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].gyroBlock.GyroOverride = false;
                    break;
                case Routine.DetermineSunPlaneNormal:
                    updateFrequency = UpdateFrequency.Update10 | UpdateFrequency.Update100;
                    break;
                case Routine.AlignPanelDownToSunPlaneNormal:
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Down);
                    updateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;
                    break;
                case Routine.AlignPanelToSun:
                    UpdateExposureValues();
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Forward);
                    rotationHelper.GenerateRotatedNormalizedVectorsAroundAxisByAngle(referenceSolarPanel.WorldMatrix.Forward,
                        referenceSolarPanel.WorldMatrix.Down, Math.Acos(mostRecentSunExposure));
                    bufferVector = rotationHelper.RotatedVectorClockwise;
                    updateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;
                    break;
                case Routine.DetermineSunOrbitDirection:
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Forward);
                    rotationHelper.GenerateRotatedNormalizedVectorsAroundAxisByAngle(referenceSolarPanel.WorldMatrix.Forward,
                        referenceSolarPanel.WorldMatrix.Down, 0.4f);
                    updateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;
                    break;
                case Routine.Debug:
                    foreach(Gyroscope gyro in registeredGyros) gyro.SetRotation(PrincipalAxis.Yaw, sunOrbit.AngularSpeedRadPS * sunOrbit.Direction);
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
        public Routine NextRoutineToMapSunOrbit() {
            Routine returnRoutine;
            if(!sunOrbit.IsMapped(SunOrbit.DataPoint.PlaneNormal)) {
                if(determineOrbitPlaneNormalManually) returnRoutine = Routine.DetermineSunPlaneNormalManual;
                else returnRoutine = Routine.DetermineSunPlaneNormal;
            }
            else if(!sunOrbit.IsMapped(SunOrbit.DataPoint.Direction)) {
                returnRoutine = Routine.AlignPanelDownToSunPlaneNormal;
                routineToResumeOn = Routine.DetermineSunOrbitDirection;
            }
            else if(!sunOrbit.IsMapped(SunOrbit.DataPoint.AngularSpeed)) {
                returnRoutine = Routine.AlignPanelDownToSunPlaneNormal;
                routineToResumeOn = Routine.DetermineSunAngularSpeed;
            }
            else {
                returnRoutine = Routine.None;
                routineToResumeOn = Routine.None;
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
                if(bufferVector.IsZero()) {
                    bufferVector = referenceSolarPanel.WorldMatrix.Forward;
                    lcd.WriteText($"Exposure value {mostRecentSunExposure} has been stored,\nas it met the precision threshold of {PRECISION_THRESHOLD}.\n" +
                        $"Please wait about five seconds before resuming to align a second time.");
                    routineToResumeOn = currentRoutine;
                    ChangeCurrentRoutine(Routine.Pause);
                }
                else {
                    sunOrbit.PlaneNormal = bufferVector.Cross(referenceSolarPanel.WorldMatrix.Forward);
                    lcd.WriteText($"Success!\nA second exposure value of {mostRecentSunExposure} has been stored.\nProceeding automatically...");
                    bufferVector = Vector3D.Zero;
                    sunOrbit.IGC_ProcessMessages();
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
                if(bufferVector.IsZero()) {
                    bufferVector = referenceSolarPanel.WorldMatrix.Forward;
                    ChangeCurrentRoutine(Routine.Pause);
                }
                else {
                    sunOrbit.PlaneNormal = bufferVector.Cross(referenceSolarPanel.WorldMatrix.Forward);
                    bufferVector = Vector3D.Zero;
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
                    ChangeCurrentRoutine(Routine.Pause);
                    return;
                }
            }
            else if((currentUpdateSource & UpdateType.Update100) != 0 && exposureDelta > 0) {
                if(exposureDeltaGreaterThanZeroOnLastRun) haveNotInvertedYetThisAxis = false;
                else exposureDeltaGreaterThanZeroOnLastRun = true;
            }
            //TODO: Make this value non-constant and able to adapt to current sunExposure progress
            float currentAngularMomentum = Math.Max((1 - mostRecentSunExposure) / 2, 0.01f) * currentSigns[(int)currentRotationAxis];
            for(int i = 0; i < registeredGyros.Count; i++) { registeredGyros[i].SetRotation(currentRotationAxis, currentAngularMomentum); }
        }
        public void DetermineSunOrbitDirection() {
            if(Gyroscope.AlignToTargetNormalizedVector(rotationHelper.RotatedVectorClockwise, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros)
                && (currentUpdateSource & UpdateType.Update100) != 0) {
                UpdateExposureValues();
                if(currentRoutineRepeats > 0) floatList.Add(Math.Sign(exposureDelta));
                currentRoutineRepeats++;
                if(floatList.Count >= MEASUREMENTS_TARGET_AMOUNT_SUN_DIRECTION) {
                    sunOrbit.Direction = Math.Sign(floatList.Average());
                    floatList.Clear();
                    ChangeCurrentRoutine(NextRoutineToMapSunOrbit());
                }
            }
        }
        public void DetermineSunAngularSpeed() {
            UpdateExposureValues();
            if(!rotationHelper.IsAlignedWithNormalizedTargetVector(sunOrbit.PlaneNormal, referenceSolarPanel.WorldMatrix.Down) || mostRecentSunExposure < 0.8f) {
                routineToResumeOn = currentRoutine;
                ChangeCurrentRoutine(Routine.AlignPanelDownToSunPlaneNormal);
            }
            else {
                if(currentRoutineRepeats > 0) floatList.Add((float)Math.Abs((Math.Acos(mostRecentSunExposure) - Math.Acos(previousSunExposure)) / (5d / 3)));
                currentRoutineRepeats++;
                //TODO: Increase the amount of data points needed or find a better algorithm that doesn't require as many data points, eventually
                if(floatList.Count >= MEASUREMENTS_TARGET_AMOUNT_SUN_SPEED) {
                    sunOrbit.AngularSpeedRadPS = floatList.Average() * sunOrbit.Direction;
                    floatList.Clear();
                    ChangeCurrentRoutine(NextRoutineToMapSunOrbit());
                }
                lcd.WriteText($"mostRecentExposure: {mostRecentSunExposure}\npreviousExposure: {previousSunExposure}\nexposureDelta: {exposureDelta}\n" +
                    $"floatListCount: {floatList.Count}\n" +
                    $"angleAcosAverage: {(floatList.Count > 0 ? floatList.Average() : 0)}\n");
            }
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
    }
}
