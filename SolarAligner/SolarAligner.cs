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
        //TODO: Implement Save() and load functionality
        //TODO: Implement ToggleActivity for SolarInstallations

        /*Setup: RotorBase=>HingeBase=>RotorTop=>Panels where panels are placed on a plane parallel to the RotorTop  e.g.:
         *RotorBase B, HingeBase H, RotorTop T, Solar Panel P, random blocks r
         * 
         *          P                   P               P                    P P P P
         *          P   H at 0° angle  P              P                      P P P P
         *          P                 P             P                        P P P P
         *          T                T            T                             T             T at 90° angle
         *          H               H           H         H at 45° angle        H
         *          B               B           B                               B
         *          r               r           r                               r
         *          r               r           r                               r
         *
         *Customdata of base Rotor must contain a NAME_SOLAR_INSTALLATION section and a value for the key ID e.g.:
         *[Solar Installation]
         *ID = Rooftop 02
         *
         *It is strongly recommended to enable "Share Inertia Tensor" for HingeBase & RotorTop, the program can't do it automatically
         */
        const string NAME_SOLAR_INSTALLATION = "Solar Installation"; //used as both an identifier as a section in Custom Data and as a Custom Name suffix/prefix
        const string NAME_PROGRAMMABLE_BLOCK = "Solar Installation Manager";
        const bool HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL = true;
        const bool SETUP_IMMEDIATELY_AFTER_REGISTRATION = true; //if true, upon successfully registering (initializing) a SolarInstallation, it will immediately set up its rotors' angles
        const bool ADD_TO_ALIGNMENT_CYCLE_UPON_SETUP = true; //if true, upon successfully initializing a SolarInstallation, it is immediately added to the alignment cycle
        const float ALIGNMENT_SUCCESS_THRESHOLD = 0.999985f;
        const int HIBERNATION_PERIOD = 180; //in multiples of 100/60~1.6667s, 36 = 1min, 180 = 5min

        readonly Func<MyCubeSize, float> maxPossibleOutputMW = gridSize => gridSize == MyCubeSize.Small ? 0.04f : 0.16f; //in MW
        readonly Func<string, string[], bool> stringEqualsArrayElementIgnoreCase = (str, strArray) => {
            for(int i = 0; i < strArray.Length; i++) {
                if(str.Equals(strArray[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }; //string.Contains(str, StringComparer) is prohibited in SE
        readonly Func<string, string> inQuotes = str => $"\"{str}\"";
        const UpdateType AUTOMATIC_UPDATE_TYPE = UpdateType.Update100 | UpdateType.Update10 | UpdateType.Update1 | UpdateType.Once;

        int hibernationTick;
        IMyTextSurface lcd;

        UpdateType currentUpdateSource;
        Routine currentRoutine;
        readonly Dictionary<string, SolarInstallation> registeredInstallationsDic = new Dictionary<string, SolarInstallation>();
        readonly HashSet<SolarInstallation> maintainedInstallationsSet = new HashSet<SolarInstallation>();
        readonly RotationHelper rotationHelper = new RotationHelper();
        readonly SunOrbit sunOrbit;

        readonly StringBuilder logger = new StringBuilder();
        readonly MyIni _ini = new MyIni();
        readonly MyCommandLine _commandLine = new MyCommandLine();
        readonly Dictionary<Routine, Action> dicRoutines;
        readonly Dictionary<string, Action> dicCommands;
        public enum Routine { None, Hibernate, ManageSolarInstallations }
        public enum PrincipalAxis { Pitch, Yaw, Roll }
        public sealed class RotationHelper {
            //The following two are mostly only useful for gyroscope rotation, as all 3 axes are available to us.
            //E.g. I want my RC.Forward to align to (0, 1, 0), I require the axes that aren't irrelevant in that rotation (Roll is), and those respective axes' planeNormals for the dot product measure
            readonly private PrincipalAxis[] _rotationAxes = new PrincipalAxis[2]; //The gyroscope operates via Pitch/Yaw/Roll, so the respective axes are stored for a given rotation task.
            readonly private Base6Directions.Direction[] _dotProductFactorDirections = new Base6Directions.Direction[2]; //As alignment is done by setting the dot product of 2 axes to 0, the directions for those two axes needs to be stored
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
            public Vector3D NormalizedVectorProjectedOntoPlane(Vector3D vecToProject, Vector3D planeNormal) {
                vecToProject.Normalize();
                planeNormal.Normalize();
                return Vector3D.Normalize(vecToProject - vecToProject.Dot(planeNormal) * planeNormal);
            }
        }
        public class Rotor {
            private const float ANGLE_LIMIT = (float)Math.PI * 2;
            public const float ALIGNMENT_PRECISION_THRESHOLD = 0.0000001f;
            readonly public IMyMotorStator terminalBlock;
            protected virtual float AngleLimit { get { return ANGLE_LIMIT; } }
            public Vector3D LocalRotationAxis { get { return terminalBlock.WorldMatrix.Up; } }
            public Rotor(IMyMotorStator terminalBlock) {
                this.terminalBlock = terminalBlock;
            }
            protected void Enable() {
                terminalBlock.Enabled = true;
                terminalBlock.Torque = 1000;
            }
            public void Unlock() {
                Enable();
                terminalBlock.RotorLock = false;
                terminalBlock.TargetVelocityRad = 0;
                terminalBlock.UpperLimitRad = float.MaxValue;
                terminalBlock.LowerLimitRad = float.MinValue;
            }
            public void Lock() {
                Enable();
                terminalBlock.RotorLock = true;
                terminalBlock.TargetVelocityRad = 0;
            }
            public double AlignToVector(RotationHelper rhInstance, Vector3D origin, Vector3D target) {
                return AlignToVector(rhInstance, origin, target, LocalRotationAxis);
            }
            /// <summary>Intended to be called only once on a non-moving rotor. Alignment measurements are imprecise when rotors are moving.</summary>
            /// <param name="alternateRotationAxis">Used instead of LocalRotationAxis to calculate the angle of rotation.</param>
            public double AlignToVector(RotationHelper rhInstance, Vector3D origin, Vector3D target, Vector3D alternateRotationAxis) {
                if(rhInstance.IsAlignedWithNormalizedTargetVector(target, origin, ALIGNMENT_PRECISION_THRESHOLD)) return 0;
                origin = rhInstance.NormalizedVectorProjectedOntoPlane(origin, alternateRotationAxis);
                target = rhInstance.NormalizedVectorProjectedOntoPlane(target, alternateRotationAxis);
                double theta = Math.Acos(origin.Dot(target));
                theta *= Math.Sign(origin.Cross(alternateRotationAxis).Dot(target));
                RotateByAngle(theta);
                return theta;
            }
            public virtual void RotateByAngle(double angleDelta) {
                float targetAngle = terminalBlock.Angle + (float)angleDelta;
                Unlock();
                if(targetAngle > AngleLimit) {
                    terminalBlock.LowerLimitRad = SignSwappedAngle(terminalBlock.Angle);
                    terminalBlock.UpperLimitRad = ClampedAngleWithinLimit(targetAngle);
                    terminalBlock.TargetVelocityRPM = 1;
                }
                else if(targetAngle < -AngleLimit) {
                    terminalBlock.UpperLimitRad = SignSwappedAngle(terminalBlock.Angle);
                    terminalBlock.LowerLimitRad = ClampedAngleWithinLimit(targetAngle);
                    terminalBlock.TargetVelocityRPM = -1;
                }
                else if(targetAngle < terminalBlock.Angle) {
                    terminalBlock.LowerLimitRad = targetAngle;
                    terminalBlock.UpperLimitRad = terminalBlock.Angle;
                    terminalBlock.TargetVelocityRPM = -1;
                }
                else {
                    terminalBlock.UpperLimitRad = targetAngle;
                    terminalBlock.LowerLimitRad = terminalBlock.Angle;
                    terminalBlock.TargetVelocityRPM = 1;
                }
            }
            private float ClampedAngleWithinLimit(float angle) {
                //Only works if 2*-AngleLimit >= angle <= 2*AngleLimit
                float returnAngle;
                if(angle < AngleLimit && angle > -AngleLimit) returnAngle = angle;
                else if(angle < 0) returnAngle = angle + AngleLimit;
                else returnAngle = angle - AngleLimit;
                return returnAngle;
            }
            protected float SignSwappedAngle(float angle) {
                float returnAngle;
                if(angle < 0) returnAngle = AngleLimit + angle;
                else returnAngle = -AngleLimit + angle;
                return returnAngle;
            }
        }
        public sealed class Hinge : Rotor {
            private const float ANGLE_LIMIT = (float)Math.PI / 2;
            protected override float AngleLimit { get { return ANGLE_LIMIT; } }
            /// <summary>
            /// Gets the top part's facing vector, if one is attached. Otherwise returns Vector3D.Zero.
            /// </summary>
            public Vector3D HingeFacing {
                get {
                    if(terminalBlock.IsAttached) return terminalBlock.Top.WorldMatrix.Left;
                    else return Vector3D.Zero;
                }
            }
            public Hinge(IMyMotorStator terminalBlock) : base(terminalBlock) { }
            public new void Unlock() {
                Enable();
                terminalBlock.RotorLock = false;
                terminalBlock.TargetVelocityRad = 0;
                terminalBlock.UpperLimitRad = AngleLimit;
                terminalBlock.LowerLimitRad = -AngleLimit;
            }
            public override void RotateByAngle(double angleDelta) {
                float targetAngle = terminalBlock.Angle + (float)angleDelta;
                Unlock();
                if(targetAngle > AngleLimit) targetAngle = AngleLimit;
                else if(targetAngle < -AngleLimit) targetAngle = -AngleLimit;
                if(targetAngle < terminalBlock.Angle) {
                    terminalBlock.LowerLimitRad = targetAngle;
                    terminalBlock.UpperLimitRad = terminalBlock.Angle;
                }
                else {
                    terminalBlock.UpperLimitRad = targetAngle;
                    terminalBlock.LowerLimitRad = terminalBlock.Angle;
                }
                terminalBlock.TargetVelocityRPM = Math.Sign(targetAngle - terminalBlock.Angle);
            }
        }
        public sealed class SunOrbit {
            //Contains the minimum 3 data points to describe a sun orbit, sufficient enough to track the sun via a rotor or gyro
            #region Fields and Properties
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
            private readonly IMyBroadcastListener broadcastREQUESTDataListener;
            private readonly IMyBroadcastListener broadcastOVERRIDEDataListener;
            private readonly bool isSolarAnalyzer;

            private Vector3D _planeNormal = Vector3D.Zero;
            private int _rotationDirection = 0; //1 is clockwise, -1 is counter clockwise, Down aligned with planeNormal https://en.wikipedia.org/wiki/Right-hand_rule#Rotations
            private float _angularSpeed = 0; //in Radians per second
            public enum DataPoint { PlaneNormal = 1, Direction, AngularSpeed }
            public Vector3D PlaneNormal {
                get { return _planeNormal; }
                set { _planeNormal = Vector3D.IsZero(value) ? _planeNormal : Vector3D.Normalize(value); }
            }
            public int RotationDirection {
                get { return _rotationDirection; }
                set { _rotationDirection = value == 1 || value == -1 ? value : _rotationDirection; }
            }
            public float AngularSpeedRadPS {
                get { return _angularSpeed; }
                set { _angularSpeed = value <= MAXIMUM_ANGULAR_SPEED && value > 0 ? value : _angularSpeed; }
            }
            #endregion
            public SunOrbit(IMyIntergridCommunicationSystem IGC, bool isSolarAnalyzer, bool fillWithMockData = false) {
                this.isSolarAnalyzer = isSolarAnalyzer;
                this.IGC = IGC;
                IGC.UnicastListener.SetMessageCallback();
                broadcastREQUESTDataListener = IGC.RegisterBroadcastListener(IGC_BROADCAST_REQUEST_DATA_TAG);
                broadcastREQUESTDataListener.SetMessageCallback();
                if(!isSolarAnalyzer) {
                    broadcastOVERRIDEDataListener = IGC.RegisterBroadcastListener(IGC_BROADCAST_OVERRIDE_DATA_TAG);
                    broadcastOVERRIDEDataListener.SetMessageCallback();
                }
                if(fillWithMockData) { PlaneNormal = new Vector3D(0, 1, 0); RotationDirection = 1; AngularSpeedRadPS = 0.001f; }
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
            #region Save & Load functions
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
            #endregion
            #region IGC functions
            //Features: Send and override broadcast that, when received by non-analyzers, replaces all their data
            //Upon receiving a data request broadcast, send a unicast message back to the requestor. Narrower query to only request from Analyzers possible
            private void IGC_OverrideDataFromMessage(MyIGCMessage message) {
                MyTuple<string, int, float> data = (MyTuple<string, int, float>)message.Data;
                Vector3D outVec;
                Vector3D.TryParse(data.Item1, out outVec);
                PlaneNormal = outVec;
                RotationDirection = data.Item2;
                AngularSpeedRadPS = data.Item3;
            }
            private MyTuple<string, int, float> IGC_GenerateMessage() {
                return new MyTuple<string, int, float>(_planeNormal.ToString(), _rotationDirection, _angularSpeed);
            }
            public void IGC_ProcessMessages() {
                if(IsMapped(DataPoint.PlaneNormal)) {
                    while(broadcastREQUESTDataListener.HasPendingMessage) {
                        MyIGCMessage requestDataMessage = broadcastREQUESTDataListener.AcceptMessage();
                        if(!((bool)requestDataMessage.Data && !isSolarAnalyzer))
                            IGC.SendUnicastMessage(requestDataMessage.Source, IGC_UNICAST_TAG, IGC_GenerateMessage());
                    }
                }
                if(!isSolarAnalyzer) while(broadcastOVERRIDEDataListener.HasPendingMessage) IGC_OverrideDataFromMessage(broadcastOVERRIDEDataListener.AcceptMessage());
                while(IGC.UnicastListener.HasPendingMessage) {
                    MyIGCMessage unicastMessage = IGC.UnicastListener.AcceptMessage();
                    if(unicastMessage.Tag == IGC_UNICAST_TAG) IGC_OverrideDataFromMessage(unicastMessage);
                }
            }
            public void IGC_BroadcastOverrideData() {
                if(IsMapped()) IGC.SendBroadcastMessage(IGC_BROADCAST_OVERRIDE_DATA_TAG, IGC_GenerateMessage());
            }
            public void IGC_BroadcastRequestData(bool requestOnlyFromAnalyzers) {
                if(!isSolarAnalyzer) IGC.SendBroadcastMessage(IGC_BROADCAST_REQUEST_DATA_TAG, requestOnlyFromAnalyzers);
            }
            #endregion
            public string PrintableDataPoints() {
                return $"{INI_KEY_PLANE_NORMAL} = {_planeNormal}\n" +
                    $"{INI_KEY_DIRECTION} = {_rotationDirection}\n" +
                    $"{INI_KEY_ANGULAR_SPEED} = {_angularSpeed}\n";
            }
            public bool PrintGPSCoordsRepresentingOrbit(IMyTextPanel lcd) {
                if(IsMapped(DataPoint.PlaneNormal)) {
                    string colorHexPlaneNormal = "#FF6900";
                    string colorHexOrbitPoint = "#FFCF00";

                    Vector3D gridaxis1 = Vector3D.CalculatePerpendicularVector(_planeNormal);
                    Vector3D gridaxis3 = Vector3D.Normalize(gridaxis1.Cross(_planeNormal));
                    Vector3D gridaxis2 = Vector3D.Normalize(gridaxis1 + gridaxis3);
                    Vector3D gridaxis4 = Vector3D.Normalize(gridaxis2.Cross(_planeNormal));

                    Vector3D[] gpsConvertables = {
                        gridaxis1,
                        gridaxis2,
                        gridaxis3,
                        gridaxis4,
                        -gridaxis1,
                        -gridaxis2,
                        -gridaxis3,
                        -gridaxis4,};
                    Func<Vector3D, string, string, string> toGPSString = (vec, coordName, colorHex) => {
                        vec.Normalize();
                        vec *= Math.Pow(10, 12);
                        return $"GPS:{coordName}:{vec.X}:{vec.Y}:{vec.Z}:{colorHex}:";
                    };
                    string printable = toGPSString(_planeNormal, "Sun Orbit Normal", colorHexPlaneNormal) + "\n";
                    printable += toGPSString(-_planeNormal, "Sun Orbit Opposite Normal", colorHexPlaneNormal) + "\n";
                    for(int i = 0; i < gpsConvertables.Length; i++) printable += toGPSString(gpsConvertables[i], $"Sun Orbit Point[{i + 1}]", colorHexOrbitPoint) + "\n";
                    lcd.WriteText(printable);
                    return true;
                }
                else return false;
            }
            public float DaytimeInMinutes() {
                return IsMapped(DataPoint.AngularSpeed) ? (float)(2 * Math.PI / _angularSpeed / 60) : float.NaN;
            }
        }
        public sealed class SolarInstallation {
            private const float MASS_LARGE_SOLAR_PANEL = 416.8f; //in kg, vanilla is 416.8kg
            private const float MASS_SMALL_SOLAR_PANEL = 143.2f; //in kg, vanilla is 143.2kg
            private const float MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL = 0.16f; //in MW, vanilla is 0.16
            private const float MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL = 0.04f; //in MW, vanilla is 0.04
            private readonly float _maxPossibleSinglePanelOutput;
            private readonly Vector3D _targetPlaneNormal;
            public enum SIStatus { Idle, AligningToNormal, AligningToSun, MatchingSunSpeed, Aligned }
            public readonly IMySolarPanel referenceSolarPanel;
            public readonly Rotor rotorBase;
            public readonly Hinge hingeBase;
            public readonly Rotor rotorTop;
            public int SolarPanelCount { get; private set; }
            public string ID { get; }
            public SIStatus Status { get; private set; }

            //TODO: RotationDirection can be defined by the sun analyzer, relative to the shipped normal vector
            int rotationDirection;

            public SolarInstallation(IMySolarPanel referenceSolarPanel, IMyMotorStator rotorBase, IMyMotorStator hingeBase, IMyMotorStator rotorTop,
                string id, int solarPanelCount) {
                this.referenceSolarPanel = referenceSolarPanel;
                this.rotorBase = rotorBase;
                this.hingeBase = hingeBase;
                this.rotorTop = rotorTop;
                ID = id;
                _maxPossibleSinglePanelOutput = referenceSolarPanel.CubeGrid.GridSizeEnum == MyCubeSize.Small ?
                    MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL : MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL;
                UpdatePanelCount(solarPanelCount);
            }
            public void UpdatePanelCount(int solarPanelCount) {
                float torque = 1000;
                SolarPanelCount = solarPanelCount;
                if(referenceSolarPanel is object) torque += 
                        (referenceSolarPanel.CubeGrid.GridSizeEnum == MyCubeSize.Large ? MASS_LARGE_SOLAR_PANEL : MASS_SMALL_SOLAR_PANEL) * solarPanelCount;
                rotorTop.terminalBlock.Torque = torque;
            }
            public void ChangeStatus(SIStatus targetStatus) {
                
            }
            public void AlignToSun(SunOrbit soInstance, RotationHelper rhInstance) {
                //TODO: Implement 0 sunshine handling and drastical sun exposure changes (e.g. when a ship's shadow blocks the ref panel partially)
                float currentSunExposure = referenceSolarPanel.MaxOutput / _maxPossibleSinglePanelOutput;
                if(currentSunExposure != 0) {
                
                }
            }
            public void AlignToNormal(RotationHelper rhInstance) {
                Status = SIStatus.AligningToNormal;

                Vector3D hingeBaseForwardOrBackward = hingeBase.terminalBlock.WorldMatrix.Forward.Dot(_targetPlaneNormal) > 
                    hingeBase.terminalBlock.WorldMatrix.Backward.Dot(_targetPlaneNormal) ?
                    hingeBase.terminalBlock.WorldMatrix.Forward : hingeBase.terminalBlock.WorldMatrix.Backward;
                double rotorBaseRotationAngle = rotorBase.AlignToVector(rhInstance, hingeBaseForwardOrBackward, _targetPlaneNormal);
                rhInstance.GenerateRotatedNormalizedVectorsAroundAxisByAngle(_targetPlaneNormal, rotorBase.LocalRotationAxis, rotorBaseRotationAngle);
                Vector3D rotatedTargetPlaneNormalVector = rhInstance.RotatedVectorClockwise.Dot(hingeBaseForwardOrBackward) >
                    rhInstance.RotatedVectorCounterClockwise.Dot(hingeBaseForwardOrBackward) ?
                    rhInstance.RotatedVectorClockwise : rhInstance.RotatedVectorCounterClockwise;
                hingeBase.AlignToVector(rhInstance, hingeBase.HingeFacing, rotatedTargetPlaneNormalVector,
                        rhInstance.NormalizedVectorProjectedOntoPlane(hingeBase.LocalRotationAxis, rotorBase.LocalRotationAxis));
            }
            public bool HasFulfilledRoutine(RotationHelper rhInstance) {
                bool returnValue = false;
                switch(Status) {
                    case SIStatus.AligningToNormal:
                        if(rhInstance.IsAlignedWithNormalizedTargetVector(_targetPlaneNormal, rotorTop.LocalRotationAxis, Rotor.ALIGNMENT_PRECISION_THRESHOLD)) {
                            rotorBase.Lock();
                            hingeBase.Lock();
                            Status = SIStatus.Idle;
                        }
                        break;
                }
                return returnValue;
            }
        }
        #region Constructor, Save & Main
        public Program() {
            Func<int, string[], string> invalidParameterMessage = (argIndex, validParams) =>
            $"Invalid parameter {inQuotes(_commandLine.Argument(argIndex))}. Valid parameters are:\n{string.Join(", ", validParams)}";
            #region Dictionary Routines
            dicRoutines = new Dictionary<Routine, Action>() {
                {Routine.None, () => { } },
                {Routine.Hibernate, () => { } },
                {Routine.ManageSolarInstallations, () => { } },
            };
            #endregion
            #region Dictionary commands
            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                {"Run", () => { ChangeCurrentRoutine(Routine.ManageSolarInstallations); } },
                {"Halt", () => ChangeCurrentRoutine(Routine.None) },
                {"Reinitialize", () => InitializeBlocks(false) },
                {"Toggle", () => {
                    string targetInstallationID = _commandLine.Argument(1);
                    SolarInstallation targetInstallation = solarInstallationList.Find(si => si.id == targetInstallationID);
                    if (!(targetInstallation is object)) {
                        Log($"{NAME_SOLAR_INSTALLATION} {inQuotes(targetInstallationID)} is not a registered construct.\nReinitialize or correct for typos.");
                        return;
                    }
                    ToggleAlignmentStatus(targetInstallation); } },
                {"ShowRegistered", () => {
                    StringBuilder message = new StringBuilder($"Currently performing routine {currentRoutine}.");
                    if (currentRoutine == Routine.AlignToSun){
                        message.Remove(message.Length-1, 1);
                        message.AppendLine(" on:");
                        foreach (var installation in aligningSolarInstallations) message.AppendLine(installation.id);
                    }
                    Log(message.ToString());
                } },
                {"ClearLog", () => {lcd.WriteText(""); logger.Clear(); } },
            };
            #endregion

            Me.CustomName = $"PB.{NAME_PROGRAMMABLE_BLOCK}";
            lcd = Me.GetSurface(0);
            lcd.ReadText(logger);
            lcd.ContentType = ContentType.TEXT_AND_IMAGE;

            InitializeBlocks(true);
            sunOrbit = new SunOrbit(IGC, false);
            sunOrbit.ReadFromIni(_ini);
            ChangeCurrentRoutine(currentRoutine);
        }
        public void Save() {
            _ini.Clear();
            sunOrbit.WriteToIni(_ini);
        }
        public void Main(string argument, UpdateType updateSource) {
            Echo(Runtime.LastRunTimeMs.ToString());
            Echo(Runtime.UpdateFrequency.ToString());
            Echo(currentRoutine.ToString() + "\n");
            currentUpdateSource = updateSource;
            if((updateSource & (UpdateType.Trigger | UpdateType.Terminal)) != 0) {
                if(_commandLine.TryParse(argument)) {
                    Action currentCommand;
                    if(dicCommands.TryGetValue(_commandLine.Argument(0), out currentCommand)) {
                        Log($"Executing command line: {string.Join(" ", _commandLine.Items)}");
                        currentCommand();
                    }
                    else {
                        StringBuilder printable = new StringBuilder("ERROR: Invalid command was passed as an argument.\nValid commands are:\n");
                        foreach(string key in dicCommands.Keys) printable.AppendLine(key);
                        Log(printable.ToString());
                    }
                }
                else dicCommands["Run"]();
            }
            else if((updateSource & UpdateType.IGC) != 0) sunOrbit.IGC_ProcessMessages();
            else dicRoutines[currentRoutine]();
        }
        #endregion
        public void ChangeCurrentRoutine(Routine targetRoutine) {
            UpdateFrequency updateFrequency = UpdateFrequency.Update100;
            switch(targetRoutine) {
                case Routine.None:
                    updateFrequency = UpdateFrequency.None;
                    break;
            }
            currentRoutine = targetRoutine;
            Runtime.UpdateFrequency = updateFrequency;
        }
        public void ManageActiveInstallations() {
            //TODO:Give feedback if there's no sunOrbit data
            if(!sunOrbit.IsMapped()) { 
                Runtime.UpdateFrequency = UpdateFrequency.None;
                return;
            }
            //TODO: Complete the code here, maybe revise on how solar installations are handled once aligned. Automatic checkup every couple of min? HasAligned --> removal from activeSolarInstallations Set?
            foreach(var si in maintainedInstallationsSet) {

            }
        }
        public void Hibernate() {
            if(hibernationTick >= HIBERNATION_PERIOD) {
                //TODO: Add code to check up on SolarInstallations, maybe add class method which checks if installation panel readout is within threshold?
                hibernationTick = 0;
                return;
            }
            hibernationTick++;
        }
        public void InitializeBlocks(bool calledInConstructor) {
            //TODO: Completely revamp this, checking if hinge and rotorTop are exactly on top of the connected rotorparts (via grid coords maybe?)
            StringBuilder logMessage = new StringBuilder($"Finished initialization. Registered {NAME_SOLAR_INSTALLATION}s:\n");
            Func<IMyTerminalBlock, bool> basePredicate = block => block.IsSameConstructAs(Me) && block.IsFunctional;
            Func<IMyTerminalBlock, Type, bool> isEqualBlockType = (block, type) => block.GetType().Name == type.Name.Substring(1);
            Func<string, string> solarInstallationName = id => $"{NAME_SOLAR_INSTALLATION}[{id}]";
            Action<IMyTerminalBlock> hideInTerminal = block => {
                block.ShowInTerminal = !HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL;
                block.ShowInToolbarConfig = !HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL;
            };

            maintainedInstallationsSet.Clear();
            var rotors = new List<IMyMotorStator>();
            GridTerminalSystem.GetBlocksOfType(rotors, block => MyIni.HasSection(block.CustomData, NAME_SOLAR_INSTALLATION) && basePredicate(block));
            //TODO: Use VectorI3 coordinates to determine whether things are in place, return feedback
            foreach(IMyMotorStator rotorBase in rotors) {
                MyIniParseResult parseResult;
                if(_ini.TryParse(rotorBase.CustomData, out parseResult)) {
                    string id = _ini.Get(NAME_SOLAR_INSTALLATION, "ID").ToString();
                    IMyMotorStator hingeBase = null;
                    IMyMotorStator rotorTop = null;
                    IMySolarPanel referencePanel = null;
                    var allPanels = new List<IMySolarPanel>();
                    if(id.Length > 0) {
                        var tempList = new List<IMyMotorStator>(1);
                        GridTerminalSystem.GetBlocksOfType(tempList, rotor => rotor.CubeGrid == rotorBase.TopGrid);
                        hingeBase = tempList.ElementAtOrDefault(0);
                    }
                    else { Log($"ERROR: Either no ID key or value in custom data of rotor {inQuotes(rotorBase.CustomName)} on grid {inQuotes(rotorBase.CubeGrid.CustomName)}."); continue; }
                    SolarInstallation duplicate = solarInstallationList.Find(si => si.ID == id);
                    if(duplicate is object) { Log($"ERROR: Rotor {inQuotes(rotorBase.CustomName)} contains a non-unique ID.\nID {inQuotes(id)} already exists in Rotor {inQuotes(duplicate.rotorBase.CustomName)}"); continue; }
                    if(hingeBase is object) {
                        var tempList = new List<IMyShipController>(1);
                        GridTerminalSystem.GetBlocksOfType(allPanels, panel => panel.CubeGrid == hingeBase.TopGrid && basePredicate(panel));
                        GridTerminalSystem.GetBlocksOfType(tempList, block => block.CubeGrid == hingeBase.TopGrid && basePredicate(block));
                    }
                    else { Log($"ERROR: Failed to find an owned, functional hinge on grid connected to rotor {inQuotes(rotorBase.CustomName)} in {solarInstallationName(id)}."); continue; }
                    if(allPanels.Count > 0) {
                        referencePanel = allPanels.ElementAt(0);
                    }
                    else { Log($"ERROR: Failed to find owned, functional solar panels on grid connected to hinge {inQuotes(hingeBase.CustomName)} in {solarInstallationName(id)}"); continue; }
                    foreach(IMySolarPanel panel in allPanels) {
                        hideInTerminal(panel);
                        panel.CustomName = $"Solar Panel.{solarInstallationName(id)}";
                    }
                    hideInTerminal(hingeBase);
                    hideInTerminal(rotorBase);
                    referencePanel.CustomName = $"Solar Panel.Reference.{solarInstallationName(id)}";
                    hingeBase.CustomName = $"Hinge.{solarInstallationName(id)}";
                    rotorBase.CustomName = $"Rotor.{solarInstallationName(id)}";
                    hingeBase.TopGrid.CustomName = $"{solarInstallationName(id)}.Solar Array";
                    rotorBase.TopGrid.CustomName = $"{solarInstallationName(id)}.RotorBase to HingeBase Connection";
                    float maxReferencePanelOutput = maxPossibleOutputMW(referencePanel.CubeGrid.GridSizeEnum);

                    SolarInstallation currentInstallation = new SolarInstallation(referencePanel, rotorBase, hingeBase, rotorTop, id, allPanels.Count);
                    solarInstallationList.Add(currentInstallation);
                    logMessage.AppendLine($"[{currentInstallation.ID}] with {allPanels.Count} functional panels");
                    if(ADD_TO_ALIGNMENT_CYCLE_UPON_SETUP) aligningSolarInstallations.Add(currentInstallation);
                }
                else { Log($"ERROR: Failed to parse custom data of rotor {inQuotes(rotorBase.CustomName)} on grid {inQuotes(rotorBase.CubeGrid.CustomName)}.\n{parseResult.Error}"); continue; }
            }
            Log(logMessage.ToString());
        }
        public T GetBlock<T>(string blockName = "", List<IMyTerminalBlock> blocks = null) where T : IMyTerminalBlock {
            var blocksLocal = blocks ?? new List<IMyTerminalBlock>(); ;
            T myBlock = (T)GridTerminalSystem.GetBlockWithName(blockName);
            if(!(myBlock is object) && !(blocks is object)) GridTerminalSystem.GetBlocks(blocksLocal);
            if(!(myBlock is object)) myBlock = (T)blocksLocal.Find(block => block.GetType().Name == typeof(T).Name.Substring(1));
            if(myBlock is object) return myBlock;
            else throw new Exception($"An owned block of type {typeof(T).Name} does not exist in the provided block list.");
        }
        public void Log(string message) {
            message = $"[{DateTime.UtcNow}] " + message;
            if(!message.EndsWith("\n")) message += "\n";
            logger.Insert(0, message);
            lcd.WriteText(logger.ToString());
        }
    }
}