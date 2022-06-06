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


namespace PXMixins_SunOrbit {
    public sealed class SunOrbit {
        //Contains the minimum 3 data points to describe a sun orbit, sufficient enough to track the sun via a rotor or gyro
        #region Fields and Properties
        private const double MAXIMUM_ANGULAR_SPEED = 1d / 60 * 2 * Math.PI;
        private const double MINIMUM_ANGULAR_SPEED = 1440d / 60 * 2 * Math.PI;
        private const string INI_SECTION_NAME = "Sun Orbit";
        private const string INI_KEY_PLANE_NORMAL = "Plane normal";
        private const string INI_KEY_ANGULAR_SPEED = "Orbital speed";
        private const string INI_KEY_DIRECTION = "Orbit direction";
        public const string IGC_BROADCAST_REQUEST_DATA_TAG = "Sun Orbit Broadcast: Request data";
        public const string IGC_BROADCAST_OVERRIDE_DATA_TAG = "Sun Orbit Broadcast: Override data";
        public const string IGC_UNICAST_TAG = "Sun Orbit Unicast";
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
        private void IGC_UpdateOwnDataFromMessage(MyIGCMessage message) {
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
        /// <returns>Whether its data was attempted to be replaced by that of a received message. TRUE if attempted, FALSE if not.</returns>
        public bool IGC_ProcessMessages() {
            bool hasUpdatedData = false;
            if(IsMapped(DataPoint.PlaneNormal)) {
                while(broadcastREQUESTDataListener.HasPendingMessage) {
                    MyIGCMessage requestDataMessage = broadcastREQUESTDataListener.AcceptMessage();
                    if(!((bool)requestDataMessage.Data && !isSolarAnalyzer))
                        IGC.SendUnicastMessage(requestDataMessage.Source, IGC_UNICAST_TAG, IGC_GenerateMessage());
                }
            }
            if(!isSolarAnalyzer) {
                hasUpdatedData = broadcastOVERRIDEDataListener.HasPendingMessage;
                while(hasUpdatedData) IGC_UpdateOwnDataFromMessage(broadcastOVERRIDEDataListener.AcceptMessage());
            }
            while(IGC.UnicastListener.HasPendingMessage) {
                MyIGCMessage unicastMessage = IGC.UnicastListener.AcceptMessage();
                if(unicastMessage.Tag == IGC_UNICAST_TAG) {
                    hasUpdatedData = true;
                    IGC_UpdateOwnDataFromMessage(unicastMessage);
                }
            }
            return hasUpdatedData;
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
}
