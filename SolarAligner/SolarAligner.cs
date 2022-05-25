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
        const float MASS_LARGE_SOLAR_PANEL = 416.8f; //in kg, vanilla is 416.8kg
        const float MASS_SMALL_SOLAR_PANEL = 143.2f; //in kg, vanilla is 143.2kg
        const float MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL = 0.16f; //in MW, vanilla is 0.16
        const float MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL = 0.04f; //in MW, vanilla is 0.04
        const bool HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL = true;
        const bool SETUP_IMMEDIATELY_AFTER_REGISTRATION = true; //if true, upon successfully registering (initializing) a SolarInstallation, it will immediately set up its rotors' angles
        const bool ADD_TO_ALIGNMENT_CYCLE_UPON_SETUP = true; //if true, upon successfully initializing a SolarInstallation, it is immediately added to the alignment cycle
        const float ALIGNMENT_SUCCESS_THRESHOLD = 0.999985f;
        const int HIBERNATION_PERIOD = 180; //in multiples of 100/60~1.6667s, 36 = 1min, 180 = 5min
        const float SETUP_SUCCESS_RANGE = 0.01f; //in radians

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
        Vector3D sunPlaneNormal;
        RotationDirection sunOrbitRotationDirection; //Rotors where rotorBase.Up.Dot(sunPlaneNormal) > 0 use this rotation direction, otherwise negative

        Routines currentRoutine = Routines.None;
        Dictionary<string, SolarInstallation> registeredInstallationsDic = new Dictionary<string, SolarInstallation>();
        HashSet<SolarInstallation> activeInstallationsSet = new HashSet<SolarInstallation>();

        StringBuilder logger = new StringBuilder();
        MyIni _ini = new MyIni();
        MyCommandLine _commandLine = new MyCommandLine();
        Dictionary<Routines, Action> dicRoutines;
        Dictionary<string, Action> dicCommands;
        public enum RotationDirection { Clockwise = 1, CounterClockwise = -1 }
        public enum SIStatus { Idle, SettingUp, AligningToSun, HasAligned }
        public enum Routines { None, Hibernate, ManageSolarInstallations }
        public class SolarInstallation {
            IMySolarPanel referenceSolarPanel;
            IMyMotorStator rotorBase;
            IMyMotorStator hingeBase;
            IMyMotorStator rotorTop;
            public int solarPanelCount { get; }
            public string ID { get; }
            float maxPossibleSinglePanelOutput;
            private float mostRecentSunExposure = 0;
            public SIStatus Status { get; private set; }
            public bool IsHibernating { get; private set; }

            //TODO: RotationDirection can be defined by the sun analyzer, relative to the shipped normal vector
            int rotationDirection = -1;

            public SolarInstallation(IMySolarPanel referenceSolarPanel, IMyMotorStator rotorBase, IMyMotorStator hingeBase, IMyMotorStator rotorTop,
                string id, int solarPanelCount) {
                this.referenceSolarPanel = referenceSolarPanel;
                this.rotorBase = rotorBase;
                this.hingeBase = hingeBase;
                this.rotorTop = rotorTop;
                ID = ID;
                maxPossibleSinglePanelOutput = referenceSolarPanel.CubeGrid.GridSizeEnum == MyCubeSize.Small ?
                    MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL : MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL;
                this.solarPanelCount = solarPanelCount;
                CalibrateTorque();
            }
            void CalibrateTorque() {
                float torque = 1000;
                if(referenceSolarPanel is object) torque += (referenceSolarPanel.CubeGrid.GridSizeEnum == MyCubeSize.Large ? MASS_LARGE_SOLAR_PANEL : MASS_SMALL_SOLAR_PANEL) * solarPanelCount;
                rotorBase.Torque = torque;
                hingeBase.Torque = torque;
                rotorTop.Torque = torque;
            }
            public void AlignToSun(float sunOrbitalPeriod, UpdateType updateSource) {
                //TODO: Implement 0 sunshine handling and drastical sun exposure changes (e.g. when a ship's shadow blocks the ref panel partially)
                float previousSunExposure = referenceSolarPanel.MaxOutput;
                mostRecentSunExposure = referenceSolarPanel.MaxOutput / maxPossibleSinglePanelOutput;
                float targetRPM = 1 / sunOrbitalPeriod + Math.Max(ALIGNMENT_SUCCESS_THRESHOLD * 3 - mostRecentSunExposure * 3, 0);
                if(mostRecentSunExposure >= ALIGNMENT_SUCCESS_THRESHOLD) {
                    targetRPM = 1 / sunOrbitalPeriod;
                    Status = SIStatus.HasAligned;
                }
                rotorTop.TargetVelocityRPM = targetRPM * rotationDirection;
                //echo($"{mostRecentSunExposure}\n{1 / sunOrbitalPeriod}\n{Math.Max(ALIGNMENT_SUCCESS_THRESHOLD * 3 - mostRecentSunExposure * 3, 0)}\n" +
                //    $"targetRPM: {targetRPM}");
            }
            public void Setup(Vector3D sunPlaneNormal) {
                Func<Vector3D, Vector3D, Vector3D> projectedOntoPlane = (vecToProject, planeNormal) => vecToProject - vecToProject.Dot(planeNormal) * planeNormal;
                //From: https://en.wikipedia.org/wiki/Rotation_matrix#Rotation_matrix_from_axis_and_angle
                Func<Vector3D, Vector3D, double, Vector3D> rotatedVector = (vecToRotate, rotAxisNormal, angle) => {
                    Vector3D rotationAxisNormalXvecToRotate = rotAxisNormal.Cross(vecToRotate);
                    return rotAxisNormal * rotationAxisNormalXvecToRotate +
                    (Math.Cos(angle) * rotationAxisNormalXvecToRotate).Cross(rotAxisNormal) +
                    Math.Sin(angle) * rotationAxisNormalXvecToRotate;
                };
                Status = SIStatus.SettingUp;

                //Stabilized vectors are required as subgrids tend to wobble
                //hingeBase.Up (orthogonal to our rotorBase.Up vector) needs to align with the interSectionLine
                Vector3D rotorBaseUp = rotorBase.WorldMatrix.Up;
                Vector3D hingeBaseUpStabilized = Vector3D.Normalize(projectedOntoPlane(hingeBase.WorldMatrix.Up, rotorBaseUp)); //is orthogonal to rotorBaseUp
                Vector3D rightSideOrthogonalVecOfHingeUp = hingeBaseUpStabilized.Cross(rotorBaseUp);
                Vector3D intersectionLine = Vector3D.Normalize(rotorBaseUp.Cross(sunPlaneNormal));
                Vector3D intersectionVecTarget = hingeBaseUpStabilized.Dot(intersectionLine) >= 0 ? intersectionLine : -intersectionLine;
                //Assuming we're facing 0 and our up is rotorBaseUp, positive is towards right, negative towards left rotation.
                //this is reversed for the right hand rule rotation using the formula above, https://en.wikipedia.org/wiki/Right-hand_rule#Rotations
                int directionSign = Math.Sign(rightSideOrthogonalVecOfHingeUp.Dot(intersectionVecTarget));
                double angularDistanceToTarget = Math.Acos(intersectionVecTarget.Dot(hingeBaseUpStabilized)) * directionSign;
                SetTargetAngle(rotorBase, false, (float)angularDistanceToTarget);

                //Align hingeBaseLeft(-X) with sunPlaneNormalTarget => angle distance OR simply take angle between rotorBaseUp & sunPlaneNormalTarget => position angle
                Vector3D sunPlaneNormalTarget = rotorBaseUp.Dot(sunPlaneNormal) >= 0 ? sunPlaneNormal : -sunPlaneNormal;
                //hingeBaseForward needs to be perpendicular to rotorBaseUp and then rotated around rotorBaseUp the angularDistanceToTarget to know whether + or - direction
                Vector3D rotatedHingeBaseForward = rotatedVector(hingeBase.WorldMatrix.Forward, hingeBaseUpStabilized, -hingeBase.Angle);
                rotatedHingeBaseForward = rotatedVector(rotatedHingeBaseForward, rotorBaseUp, -angularDistanceToTarget);
                directionSign = Math.Sign(rotatedHingeBaseForward.Dot(sunPlaneNormalTarget));
                double targetAngle = Math.Acos(rotorBaseUp.Dot(sunPlaneNormalTarget)) * directionSign;
                SetTargetAngle(hingeBase, true, (float)targetAngle);
            }
            static void SetTargetAngle(IMyMotorStator rotorToSet, bool isHinge, float angularTarget) {
                //treats angularTarget as a position if isHinge, otherwise treats angularTarget as an angular distance to travel (in the negative or positive direction)
                //Rotors & Hinges overshoot their limit if their speed is quick and/or their acceleration powerful enough
                //NOTE: Without anything but the rotor connected to the hinge top, at acceleration 2kN and 1RPM it seems to do fine consistently
                //Hinge limit needs to be between -90° and 90° (0.5Pi rad)
                //Rotor limit needs to be between -360° and 360°(2Pi rad) (-+361° is unlimited)
                rotorToSet.RotorLock = false;
                rotorToSet.Enabled = true;
                float maxAngle = (float)(isHinge ? Math.PI / 2 : Math.PI * 2);
                float currentAngle = rotorToSet.Angle;
                float targetAngle = angularTarget;
                //Rotors are bad at rotating efficiently below -maxAngle, e.g. -350 -> -370 = -10 = 350 is a 350° movement
                //-Angle + maxAngle = +Angle
                if(!isHinge) {
                    targetAngle = currentAngle + angularTarget;
                    if(targetAngle > maxAngle) targetAngle -= 2 * maxAngle;
                    else if(targetAngle < -maxAngle) targetAngle += maxAngle;
                }
                //Order in which they're applied is relevant, as UpperLimit >= LowerLimit
                rotorToSet.UpperLimitRad = targetAngle;
                rotorToSet.LowerLimitRad = targetAngle;
                rotorToSet.UpperLimitRad = targetAngle;
                if(!float.IsNaN(targetAngle)) rotorToSet.TargetVelocityRPM = Math.Sign(targetAngle + maxAngle - (currentAngle + maxAngle));
                if(isHinge) {
                    //Hinges ignore their physical limits when trying to get back within bounds (e.g. 80° -> -80° will result in the hinge getting stuck at ~113°).
                    //Leave a buffer behind our target angle, so that it doesn't affect our actual targetAngle
                    if(targetAngle < currentAngle) rotorToSet.UpperLimitRad = targetAngle + 0.00001f;
                    else if(targetAngle > currentAngle) rotorToSet.LowerLimitRad = targetAngle - 0.00001f;
                }
                //Echo($"UpperLimit: {rotorToSet.UpperLimitRad}\nLowerLimit: {rotorToSet.LowerLimitRad}");
                //Echo($"angTarget: {angularTarget}");
                //Echo($"isHinge: {isHinge}");
                //Echo($"currentAngle: {rotorToSet.Angle}\ntargetAngle: {targetAngle}");
                //Echo($"RPM: {rotorToSet.TargetVelocityRPM}\n");
            }
            public void HasSetup() {
                bool rotorBaseInPlace = Math.Round(rotorBase.Angle) == rotorBase.LowerLimitDeg;
                bool hingeBaseInPlace = hingeBase.Angle == hingeBase.LowerLimitRad || hingeBase.Angle == hingeBase.UpperLimitRad;
                rotorBase.RotorLock = rotorBaseInPlace;
                hingeBase.RotorLock = hingeBaseInPlace;
                if(rotorBaseInPlace && hingeBaseInPlace) Status = SIStatus.AligningToSun;
            }
        }
        public Program() {
            Func<int, string[], string> invalidParameterMessage = (argIndex, validParams) =>
            $"Invalid parameter {inQuotes(_commandLine.Argument(argIndex))}. Valid parameters are:\n{string.Join(", ", validParams)}";

            Me.CustomName = $"PB.{NAME_PROGRAMMABLE_BLOCK}";
            lcd = Me.GetSurface(0);
            lcd.ReadText(logger);
            lcd.ContentType = ContentType.TEXT_AND_IMAGE;

            InitializeBlocks();
            dicRoutines = new Dictionary<Routines, Action>() {
                {Routines.None, () => { } },
                {Routines.Hibernate, () => { } },
                {Routines.ManageSolarInstallations, () => { } },
            };
            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                {"Run", () => { if(aligningSolarInstallations.Count > 0)ChangeCurrentRoutine(Routines.AlignToSun, UpdateFrequency.Update100); } },
                {"Halt", () => ChangeCurrentRoutine(Routines.None, UpdateFrequency.None) },
                {"Reinitialize", () => InitializeBlocks() },
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
                    if (currentRoutine == Routines.AlignToSun){
                        message.Remove(message.Length-1, 1);
                        message.AppendLine(" on:");
                        foreach (var installation in aligningSolarInstallations) message.AppendLine(installation.id);
                    }
                    Log(message.ToString());
                } },
                {"ClearLog", () => {lcd.WriteText(""); logger.Clear(); } },
            };
        }
        public void Save() {
        }
        public void Main(string argument, UpdateType updateSource) {
            Echo(Runtime.LastRunTimeMs.ToString());
            //TODO: Manage arguments so that "Run" can become "", being the default when run is initiated via non-automating updating
            if((updateSource & AUTOMATIC_UPDATE_TYPE) == 0) {
                if(_commandLine.TryParse(argument)) {
                    Action currentCommand;
                    if(dicCommands.TryGetValue(_commandLine.Argument(0), out currentCommand)) {
                        string cmdLine = _commandLine.Argument(0);
                        for(int i = 1; i < _commandLine.ArgumentCount; i++) {
                            cmdLine += $" {_commandLine.Argument(i)}";
                        }
                        Log($"Executing command line: {cmdLine}");
                        currentCommand();
                    }
                    else {
                        StringBuilder printable = new StringBuilder("ERROR: Invalid command was passed as an argument.\nValid commands are:\n");
                        foreach(string key in dicCommands.Keys) printable.AppendLine(key);
                        Log(printable.ToString());
                    }
                }
            }
            else dicRoutines[currentRoutine]();
        }
        public void ChangeCurrentRoutine(Routines targetRoutine, UpdateFrequency updateFrequency) {
            currentRoutine = targetRoutine;
            Runtime.UpdateFrequency = updateFrequency;
        }
        public void ManageActiveInstallations() {
            //TODO:Give feedback if there's no sunOrbit data
            if(!HasSunOrbitData()) return;
            //TODO: Complete the code here, maybe revise on how solar installations are handled once aligned. Automatic checkup every couple of min? HasAligned --> removal from activeSolarInstallations Set?
            foreach(var si in activeInstallationsSet) {
                switch(si.Status) {
                    case SIStatus.Idle:
                        si.Setup(sunPlaneNormal);
                        continue;
                    case SIStatus.SettingUp:
                        si.Setup(sunPlaneNormal);
                        continue;
                    case SIStatus.AligningToSun:
                        si.Setup(sunPlaneNormal);
                        continue;
                    case SIStatus.HasAligned:
                        si.Setup(sunPlaneNormal);
                        continue;
                }
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
        public bool HasSunOrbitData() {
            bool returnBool = false;
            //TODO: Code here. Check Program's fields, then check Storage/Custom Data if not yet assigned
        }
        public void GenerateGPSCoords() {
            //TODO: Optimize by providing feedback, giving a viable lcd to print to and give options between 4, 8 or 16 points to print
            string colorHex = "#ff8c00";

            Vector3D gridaxis1 = Vector3D.CalculatePerpendicularVector(sunPlaneNormal);
            Vector3D gridaxis3 = Vector3D.Normalize(gridaxis1.Cross(sunPlaneNormal));
            Vector3D gridaxis2 = Vector3D.Normalize(gridaxis1 + gridaxis3);
            Vector3D gridaxis4 = Vector3D.Normalize(gridaxis2.Cross(sunPlaneNormal));

            Vector3D[] gpsConvertables = {
                gridaxis1,
                gridaxis2,
                gridaxis3,
                gridaxis4,
                -gridaxis1,
                -gridaxis2,
                -gridaxis3,
                -gridaxis4,
            };
            //Func<> doesn't work when coordinates are super tiny
            Func<Vector3D, string, string> toGPSString = (vec, coordName) => $"GPS:{coordName}:{vec.X}:{vec.Y}:{vec.Z}:{colorHex}:";
            string printable = "";
            for(int i = 0; i < gpsConvertables.Length; i++) {
                printable += toGPSString(Vector3D.Normalize(gpsConvertables[i]) * Math.Pow(10, 12), $"gridPointOnSunOrbit{i + 1}") + "\n";
            }
            var lcd = (IMyTextPanel)GridTerminalSystem.GetBlockWithName("LCD Panel");
            lcd.WriteText(printable);
        }
        public void InitializeBlocks() {
            //TODO: Completely revamp this, checking if hinge and rotorTop are exactly on top of the connected rotorparts (via grid coords maybe?)
            StringBuilder logMessage = new StringBuilder($"Finished initialization. Registered {NAME_SOLAR_INSTALLATION}s:\n");
            Func<IMyTerminalBlock, bool> basePredicate = block => block.IsSameConstructAs(Me) && block.IsFunctional;
            Func<IMyTerminalBlock, Type, bool> isEqualBlockType = (block, type) => block.GetType().Name == type.Name.Substring(1);
            Func<string, string> solarInstallationName = id => $"{NAME_SOLAR_INSTALLATION}[{id}]";
            Action<IMyTerminalBlock> hideInTerminal = block => {
                block.ShowInTerminal = !HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL;
                block.ShowInToolbarConfig = !HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL;
            };

            activeInstallationsSet.Clear();
            var rotors = new List<IMyMotorStator>();
            GridTerminalSystem.GetBlocksOfType(rotors, block => MyIni.HasSection(block.CustomData, NAME_SOLAR_INSTALLATION) && basePredicate(block));
            foreach(IMyMotorStator rotorBase in rotors) {
                MyIniParseResult parseResult;
                if(_ini.TryParse(rotorBase.CustomData, out parseResult)) {
                    string id = _ini.Get(NAME_SOLAR_INSTALLATION, "ID").ToString();
                    IMyMotorStator hingeBase = null;
                    IMyMotorStator rotorTop = null;
                    IMySolarPanel referencePanel = null;
                    IMyShipController massSensor = null;
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
                        massSensor = tempList.ElementAtOrDefault(0);
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
                    if(massSensor is object) {
                        hideInTerminal(massSensor);
                        massSensor.CustomName = $"Mass Sensor.{solarInstallationName(id)}";
                    }

                    SolarInstallation currentInstallation = new SolarInstallation(referencePanel, rotorBase, hingeBase, rotorTop, id, allPanels.Count, massSensor);
                    solarInstallationList.Add(currentInstallation);
                    string withOrWithout = massSensor is object ? "with" : "without";
                    logMessage.AppendLine($"[{currentInstallation.ID}] {withOrWithout} mass sensor and {allPanels.Count} functional panels");
                    if(ADD_TO_ALIGNMENT_CYCLE_UPON_SETUP) aligningSolarInstallations.Add(currentInstallation);
                }
                else { Log($"ERROR: Failed to parse custom data of rotor {inQuotes(rotorBase.CustomName)} on grid {inQuotes(rotorBase.CubeGrid.CustomName)}.\n{parseResult.Error}"); continue; }
            }
            Log(logMessage.ToString());
        }
        public void Log(string message) {
            message = $"[{DateTime.UtcNow}] " + message;
            if(!message.EndsWith("\n")) message += "\n";
            logger.Insert(0, message);
            lcd.WriteText(logger.ToString());
        }
    }
}