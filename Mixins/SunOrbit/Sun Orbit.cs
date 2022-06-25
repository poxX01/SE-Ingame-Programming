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
    public sealed class SunOrbit {
        //Contains the minimum 3 data points to describe a sun orbit, sufficient enough to track the sun via a rotor or gyro
        #region Fields and Properties
        private const double MAXIMUM_ANGULAR_SPEED = 1d / 60 * 2 * Math.PI;
        private const double MINIMUM_ANGULAR_SPEED = 1440d / 60 * 2 * Math.PI;
        private const string INI_SECTION_NAME = "Sun Orbit";
        private const string INI_KEY_PLANE_NORMAL = "Plane normal";
        private const string INI_KEY_ANGULAR_SPEED = "Orbital speed";
        private const string INI_KEY_DIRECTION = "Orbit direction";
        private const string INI_KEY_STORED_TRANSMISSIONS = "Stored transmissions";
        public const string IGC_BROADCAST_REQUEST_DATA_TAG = "Sun Orbit Broadcast: Request data";
        public const string IGC_BROADCAST_OVERRIDE_DATA_TAG = "Sun Orbit Broadcast: Override data";
        public const string IGC_UNICAST_TAG = "Sun Orbit Unicast: Receive data";
        private readonly IMyIntergridCommunicationSystem IGC;
        private readonly IMyBroadcastListener broadcastREQUESTDataListener;
        private readonly IMyBroadcastListener broadcastOVERRIDEDataListener;
        private readonly bool isSolarAnalyzer;
        private readonly HashSet<MyIGCMessage> savedMessages = new HashSet<MyIGCMessage>();

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
            broadcastREQUESTDataListener = IGC.RegisterBroadcastListener(IGC_BROADCAST_REQUEST_DATA_TAG);
            broadcastREQUESTDataListener.SetMessageCallback(IGC_BROADCAST_REQUEST_DATA_TAG);
            if(!isSolarAnalyzer) {
                IGC.UnicastListener.SetMessageCallback(IGC_UNICAST_TAG);
                broadcastOVERRIDEDataListener = IGC.RegisterBroadcastListener(IGC_BROADCAST_OVERRIDE_DATA_TAG);
                broadcastOVERRIDEDataListener.SetMessageCallback(IGC_BROADCAST_OVERRIDE_DATA_TAG);
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
                    _rotationDirection = 0;
                    break;
                case DataPoint.Direction:
                    goto case DataPoint.PlaneNormal;
                case DataPoint.AngularSpeed:
                    _angularSpeed = 0;
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
        public class IGC_Message {
            public bool fromSolarAnalyzer;

            public Vector3D planeNormal;
            public int rotationDirection;
            public float angularSpeed;

            public IGC_Message(MyIGCMessage message) {
                MyTuple<string, string, int, float> messageData = (MyTuple<string, string, int, float>)message.Data;

            }
        }

        private void IGC_UpdateOwnDataFromMessage(MyIGCMessage message) {
            MyTuple<string, int, float> data = (MyTuple<string, int, float>)message.Data;
            Vector3D messageVector = Vector3D.Zero;
            Vector3D.TryParse(data.Item1, out messageVector);

            PlaneNormal = messageVector;
            RotationDirection = data.Item2;
            AngularSpeedRadPS = data.Item3;

        }
        private MyTuple<string, int, float> IGC_GenerateMessage() {
            return new MyTuple<string, int, float>(_planeNormal.ToString(), _rotationDirection, _angularSpeed);
        }
        /// <returns>Whether its data was replaced by that of a received message. TRUE if replaced, FALSE if not.</returns>
        public void IGC_ProcessMessages() {
            //TODO: Maybe store an antenna for each soInstance so than we can ensure an antenna present and modify it (test and investigate how the IGC works first ingame i guess to see whether
            //      that's necessary

            //only override own data when the receiving data is the same amount of mapped or more mapped
            //TODO: save request sender id when received(data = receiveOnlyFromAnalyzers can be processed right then and there, decie if we need to save the id),
            //      then supply with data once we ourselves have gotten
            //TODO: Allow for a switch setting: to force override even when the received data is less mapped than stored one (this needs to be integrated into the request message and thusly stored)

            //TODO: While signals are cached here, they are lost upon reloading the save. Reevaluate whether we should commit to saving messages or just discard them all the way
            //      since we're sending out a message as soon as we have an orbit anyway. Maybe have non-analyzers send out override too, but have a soft override and HARD override?
            //      Saving signals doesn't make much sense (we always send an override signal at mapping 1 and 3 AND SIMs propagate the override signal anyway

            //TODO: Maybe instead of using a tuple make a seperate data class, as a lot of data is coming up

            while(broadcastREQUESTDataListener.HasPendingMessage) {
                MyIGCMessage requestDataMessage = broadcastREQUESTDataListener.AcceptMessage();
                var messageData = requestDataMessage.As<MyTuple<bool, string>>();
                if(IsMapped(DataPoint.PlaneNormal)) IGC.SendUnicastMessage(requestDataMessage.Source, IGC_UNICAST_TAG, IGC_GenerateMessage());
            }

            if(!isSolarAnalyzer) {
                while(broadcastOVERRIDEDataListener.HasPendingMessage) IGC_UpdateOwnDataFromMessage(broadcastOVERRIDEDataListener.AcceptMessage());

                var messagesFromSolarAnalyzers = new List<MyIGCMessage>();
                var messagesFromNonAnalyzers = new List<MyIGCMessage>();
                while(IGC.UnicastListener.HasPendingMessage) {
                    MyIGCMessage unicastMessage = IGC.UnicastListener.AcceptMessage();
                    if(unicastMessage.Tag == IGC_UNICAST_TAG) {
                        if((bool)unicastMessage.Data) messagesFromSolarAnalyzers.Add(unicastMessage);
                        else messagesFromNonAnalyzers.Add(unicastMessage);
                    }
                }
                messagesFromNonAnalyzers.AddRange(messagesFromSolarAnalyzers);
                for(int i = 0; i < messagesFromNonAnalyzers.Count; i++) IGC_UpdateOwnDataFromMessage(messagesFromNonAnalyzers[i]);
            }
        }
        //TEST: IGC transmission distances and receiving
        public void IGC_BroadcastOverrideData() {
            if(IsMapped()) IGC.SendBroadcastMessage(IGC_BROADCAST_OVERRIDE_DATA_TAG, IGC_GenerateMessage(), TransmissionDistance.TransmissionDistanceMax);
            //TODO: Force propagate this override signal as a Solar Installation Manager to other SIMs, which will require careful storing of source IDs
            //      as we don't wanna loop constantly sending back and forth
        }
        public void IGC_BroadcastRequestData(string gridNameIdentifier) {
            if(!isSolarAnalyzer) IGC.SendBroadcastMessage(IGC_BROADCAST_REQUEST_DATA_TAG, new MyTuple<bool, string>(isSolarAnalyzer, gridNameIdentifier));
        }
        #endregion
        public string PrintableDataPoints() {
            return $"{INI_KEY_PLANE_NORMAL} = {_planeNormal}\n" +
                $"{INI_KEY_DIRECTION} = {_rotationDirection}\n" +
                $"{INI_KEY_ANGULAR_SPEED} = {_angularSpeed}\n";
        }
        public bool PrintGPSCoordsRepresentingOrbit(IMyTextPanel lcd) {
            if(IsMapped(DataPoint.PlaneNormal)) {
                lcd.ContentType = ContentType.TEXT_AND_IMAGE;

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
    }
}
