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
        //TODO: Save instatement marking, so that recompile can be used for reinitialize
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
         *
         */
        const string INI_SECTION_NAME = "Solar Installation Manager";
        const string INI_KEY_REGISTERED_IDs = "Registered IDs";
        const string INI_KEY_CURRENT_ROUTINE = "Current routine";
        const string INI_KEY_INSTATING_IDs = "Instating IDs";

        const float ALIGNMENT_SUCCESS_THRESHOLD = 0.999995f;

        UpdateType currentUpdateSource;
        Routine currentRoutine;
        readonly Dictionary<string, SolarInstallation> registeredInstallationsDic = new Dictionary<string, SolarInstallation>();
        readonly HashSet<SolarInstallation> instatingInstallationsSet = new HashSet<SolarInstallation>();
        readonly SunOrbit soInstance;

        readonly Logger logger;
        readonly MyIni _ini = new MyIni();
        readonly MyCommandLine _commandLine = new MyCommandLine();
        readonly Dictionary<Routine, Action> dicRoutines;
        readonly Dictionary<string, Action> dicCommands;
        readonly Dictionary<string, string> dicCommandsDocumentation;
        public enum Routine { None, ManageSolarInstallations }
        public sealed class SolarInstallation {
            private const float MASS_LARGE_SOLAR_PANEL = 416.8f; //in kg, vanilla is 416.8kg
            private const float MASS_SMALL_SOLAR_PANEL = 143.2f; //in kg, vanilla is 143.2kg
            private const float MASS_LARGE_OXYGEN_FARM = 3004; //in kg, vanilla is 3004
            private const float MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL = 0.16f; //in MW, vanilla is 0.16
            private const float MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL = 0.04f; //in MW, vanilla is 0.04
            private const float MAX_POSSIBLE_OUTPUT_LARGE_OXYGEN_FARM = 1; //Seems to be a percentage, 1 being the max 0.3L/s

            public const string INI_SECTION_PREFIX = "Solar Installation";
            private readonly string fullIniSectionName;
            private const string INI_KEY_STATUS = "Status";

            private float _maxSolPanelOutput;
            private float _sunExposureCache;
            private Vector3D _targetSunAlignmentVec;
            private Vector3D _targetOrbitPlaneNormal;
            private int _localRotationDirection;

            private int _routineCounter;
            public enum SIStatus { Decommissioned = -1, Idle, AligningToOrbitPlaneNormal, AligningToSun, MatchingSunRotation, Aligned }
            private IMySolarPanel refSolPanel;
            private IMyOxygenFarm refOxyFarm;
            public RotationHelper rhLocal;
            public readonly Rotor rotorBase;
            public readonly Hinge hingeBase;
            public readonly Rotor rotorTop;
            public int SolarPanelCount { get; private set; }
            public int OxygenFarmCount { get; private set; }
            public bool IsPlaneNormalAligned {
                get { return rhLocal.IsAlignedWithNormalizedTargetVector(_targetOrbitPlaneNormal, hingeBase.HingeFacing, 0.00001f); }
            }
            public bool HasSolarHarvester {
                get { return SolarPanelCount > 0 || OxygenFarmCount > 0; }
            }
            public bool HasWorkingStatus {
                get { return Status > 0 && Status != SIStatus.Aligned; }
            }
            public bool IsValidStructure {
                get {
                    return rotorBase.terminalBlock is object &&
                      hingeBase.terminalBlock is object &&
                      rotorTop.terminalBlock is object &&
                      !rotorBase.terminalBlock.Closed &&
                      !hingeBase.terminalBlock.Closed &&
                      !rotorTop.terminalBlock.Closed;
                }
            }
            public string ID { get; }
            public SIStatus Status { get; private set; }
            private float MeasuredSunExposure {
                get {
                    if(refSolPanel is object) return refSolPanel.MaxOutput / _maxSolPanelOutput;
                    else return refOxyFarm.GetOutput() / MAX_POSSIBLE_OUTPUT_LARGE_OXYGEN_FARM;
                }
            }
            public readonly static Dictionary<SIStatus, string> printableStatus = new Dictionary<SIStatus, string>() {
                { SIStatus.AligningToOrbitPlaneNormal, "Aligning to the solar orbit plane normal" },
                { SIStatus.AligningToSun, "Aligning to the sun" },
                { SIStatus.MatchingSunRotation, "Aligning to the sun"},
            };
            #region Constructor & initialization functions
            public SolarInstallation(SunOrbit soInstance, IMyMotorStator rotorBase, IMyMotorStator hingeBase, IMyMotorStator rotorTop,
                string id, IMyGridTerminalSystem GTS) {
                this.rotorBase = new Rotor(rotorBase);
                this.hingeBase = new Hinge(hingeBase);
                this.rotorTop = new Rotor(rotorTop);
                rhLocal = new RotationHelper();
                ID = id;
                RenameAndHide(rotorBase, "Rotor base");
                RenameAndHide(hingeBase, "Hinge base");
                RenameAndHide(rotorTop, "Rotor top");
                hingeBase.SetValueBool("ShareInertiaTensor", true);
                rotorTop.SetValueBool("ShareInertiaTensor", true);
                rotorBase.TopGrid.CustomName = $"[SI.{ID}]Rotor base.Top";
                hingeBase.TopGrid.CustomName = $"[SI.{ID}]Hinge base.Top";
                rotorTop.TopGrid.CustomName = $"[SI.{ID}]Solar harvester grid";
                fullIniSectionName = $"{INI_SECTION_PREFIX}.{id}";
                UpdateHarvesterCounts(GTS);
                LocalizeSolarOrbitInfo(soInstance);
            }
            private void RenameAndHide(IMyTerminalBlock block, string targetName) {
                block.ShowInInventory = false;
                block.ShowInTerminal = false;
                block.ShowInToolbarConfig = false;
                block.ShowOnHUD = false;
                block.CustomName = $"[SI.{ID}]{targetName}";
            }
            public void UpdateHarvesterCounts(IMyGridTerminalSystem GTS) {
                //TODO: Allow for reference panel customization (search for custom data section first, in case one ought be specified, then possibly read custom max values)
                //      Append their modified values (if one from custom data was used) in the log somewhere
                float torque = 1000;
                var solarPanelList = new List<IMySolarPanel>();
                var oxygenFarmList = new List<IMyOxygenFarm>();
                GTS.GetBlocksOfType(solarPanelList, solPanel => solPanel.IsFunctional && solPanel.CubeGrid == rotorTop.terminalBlock.TopGrid);
                GTS.GetBlocksOfType(oxygenFarmList, oxyFarm => oxyFarm.IsFunctional && oxyFarm.CubeGrid == rotorTop.terminalBlock.TopGrid);
                SolarPanelCount = solarPanelList.Count;
                OxygenFarmCount = oxygenFarmList.Count;
                solarPanelList.ForEach(solPanel => RenameAndHide(solPanel, "Solar Panel"));
                oxygenFarmList.ForEach(oxyFarm => RenameAndHide(oxyFarm, "Oxygen Farm"));
                if(SolarPanelCount > 0) {
                    refSolPanel = solarPanelList.Find(panel => MyIni.HasSection(panel.CustomData, INI_SECTION_PREFIX)) ?? solarPanelList[0];
                    MyCubeSize gridSize = refSolPanel.CubeGrid.GridSizeEnum;
                    _maxSolPanelOutput = gridSize == MyCubeSize.Large ? MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL : MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL;
                    torque += SolarPanelCount * (gridSize == MyCubeSize.Large ? MASS_LARGE_SOLAR_PANEL : MASS_SMALL_SOLAR_PANEL);
                }
                if(OxygenFarmCount > 0) {
                    refOxyFarm = oxygenFarmList.Find(oxyFarm => MyIni.HasSection(oxyFarm.CustomData, INI_SECTION_PREFIX)) ?? oxygenFarmList[0];
                    torque += OxygenFarmCount * MASS_LARGE_OXYGEN_FARM;
                }
                rotorTop.terminalBlock.Torque = torque;
            }
            #endregion
            public void ChangeStatus(SIStatus targetStatus) {
                _routineCounter = 0;
                switch(targetStatus) {
                    default:
                        rotorBase.Lock();
                        hingeBase.Lock();
                        rotorTop.Lock();
                        break;
                    case SIStatus.AligningToOrbitPlaneNormal:
                        rotorBase.Unlock();
                        hingeBase.Unlock();
                        Vector3D hingeBaseForwardOrBackward = hingeBase.terminalBlock.WorldMatrix.Forward.Dot(_targetOrbitPlaneNormal) >=
                            hingeBase.terminalBlock.WorldMatrix.Backward.Dot(_targetOrbitPlaneNormal) ?
                            hingeBase.terminalBlock.WorldMatrix.Forward : hingeBase.terminalBlock.WorldMatrix.Backward;
                        double rotorBaseRotationAngle = rotorBase.AlignToVector(rhLocal, hingeBaseForwardOrBackward, _targetOrbitPlaneNormal);
                        rhLocal.GenerateRotatedNormalizedVectorsAroundAxisByAngle(_targetOrbitPlaneNormal, rotorBase.LocalRotationAxis, rotorBaseRotationAngle);
                        Vector3D rotatedTargetPlaneNormalVector = rhLocal.RotatedVectorClockwise.Dot(hingeBaseForwardOrBackward) >
                            rhLocal.RotatedVectorCounterClockwise.Dot(hingeBaseForwardOrBackward) ?
                            rhLocal.RotatedVectorClockwise : rhLocal.RotatedVectorCounterClockwise;
                        hingeBase.AlignToVector(rhLocal, rotorTop.LocalRotationAxis, rotatedTargetPlaneNormalVector,
                                rhLocal.NormalizedVectorProjectedOntoPlane(hingeBase.LocalRotationAxis, rotorBase.LocalRotationAxis));
                        break;
                    case SIStatus.AligningToSun:
                        rotorTop.Unlock();
                        Vector3D alignmentVec = refSolPanel.WorldMatrix.Forward;
                        float currentSunExposure = MeasuredSunExposure;
                        rhLocal.GenerateRotatedNormalizedVectorsAroundAxisByAngle(alignmentVec, _targetOrbitPlaneNormal, Math.Acos(currentSunExposure));
                        rotorTop.AlignToVector(rhLocal, alignmentVec, rhLocal.RotatedVectorClockwise);
                        _sunExposureCache = currentSunExposure;
                        _targetSunAlignmentVec = rhLocal.RotatedVectorClockwise;
                        break;
                    case SIStatus.MatchingSunRotation:
                        rotorTop.Unlock();
                        break;
                    case SIStatus.Aligned:
                        break;
                }
                Status = targetStatus;
            }
            public void ExecuteStatusRoutine(SunOrbit soInstance) {
                switch(Status) {
                    case SIStatus.AligningToOrbitPlaneNormal:
                        if(IsPlaneNormalAligned) {
                            if(_routineCounter < 3) {
                                _routineCounter++;
                                return;
                            }
                            SIStatus targetStatus = soInstance.IsMapped() && HasSolarHarvester ? SIStatus.AligningToSun : SIStatus.Idle;
                            rotorBase.Lock();
                            hingeBase.Lock();
                            ChangeStatus(targetStatus);
                        }
                        break;
                    case SIStatus.AligningToSun:
                        AlignToSun();
                        break;
                    case SIStatus.MatchingSunRotation:
                        MatchSunRotation(soInstance);
                        break;
                }
            }
            public void MatchSunRotation(SunOrbit soInstance) {
                float currentSunExposure = MeasuredSunExposure;
                if(currentSunExposure < 0.997f) ChangeStatus(SIStatus.AligningToSun);
                else if(currentSunExposure < ALIGNMENT_SUCCESS_THRESHOLD) rotorTop.terminalBlock.TargetVelocityRad = 2 * soInstance.AngularSpeedRadPS * _localRotationDirection;
                else {
                    rotorTop.terminalBlock.TargetVelocityRad = soInstance.AngularSpeedRadPS * _localRotationDirection;
                    ChangeStatus(SIStatus.Aligned);
                }
                //TODO: Overhaul the speed of this, maybe scale it with the overall daytime
                //TEST: in quick orbit ones (maybe 30min)
            }
            public void AlignToSun() {
                //TEST: in quick solar orbits
                float currentSunExposure = MeasuredSunExposure;
                Vector3D alignmentVec = refSolPanel.WorldMatrix.Forward;

                if(currentSunExposure > 0.999f && rhLocal.IsAlignedWithNormalizedTargetVector(_targetSunAlignmentVec, alignmentVec)) ChangeStatus(SIStatus.MatchingSunRotation);
                else if(currentSunExposure > 0) {
                    _routineCounter++;
                    if(_routineCounter == 3) _targetSunAlignmentVec = currentSunExposure > _sunExposureCache ? _targetSunAlignmentVec : rhLocal.RotatedVectorCounterClockwise;
                    rotorTop.AlignToVector(rhLocal, alignmentVec, _targetSunAlignmentVec);
                }
            }
            public void LocalizeSolarOrbitInfo(SunOrbit soInstance) {
                if(soInstance.IsMapped(SunOrbit.DataPoint.PlaneNormal))
                    _targetOrbitPlaneNormal = rotorBase.LocalRotationAxis.Dot(soInstance.PlaneNormal) >= 0 ? soInstance.PlaneNormal : -soInstance.PlaneNormal;
                if(soInstance.IsMapped(SunOrbit.DataPoint.Direction))
                    _localRotationDirection = _targetOrbitPlaneNormal == soInstance.PlaneNormal ? -soInstance.RotationDirection : soInstance.RotationDirection;
                //Rotors use their UP instead of their DOWN as rotation axes, so they are inversed here
            }
            #region INI write & read
            public void WriteToIni(MyIni ini) {
                ini.Set(fullIniSectionName, INI_KEY_STATUS, (int)Status);
            }
            public void ReadFromIni(MyIni ini) {
                Status = (SIStatus)ini.Get(fullIniSectionName, INI_KEY_STATUS).ToInt32();
            }
            #endregion
        }
        #region Constructor, Save & Main
        public Program() {
            soInstance = new SunOrbit(IGC, false);
            logger = new Logger(Me, INI_SECTION_NAME, GridTerminalSystem);

            #region Dictionary Routines
            dicRoutines = new Dictionary<Routine, Action>() {
                {Routine.None, () => { } },
                {Routine.ManageSolarInstallations, () => ManageActiveInstallations() },
            };
            #endregion
            #region Dictionary commands
            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                {"Run", () => {
                    bool instateFlag = _commandLine.Switch("i") || _commandLine.Switch("instate");
                    Run(instateFlag);
                } },
                {"Halt", () => {
                    ChangeCurrentRoutine(Routine.None);
                    if (instatingInstallationsSet.Count > 0) {
                        bool singular = instatingInstallationsSet.Count == 1;
                        logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", instatingInstallationsSet)}\" " +
                            $"{GrammaticalNumber("has", singular)} been set to idle.");
                        foreach (SolarInstallation SI in instatingInstallationsSet) SI.ChangeStatus(SolarInstallation.SIStatus.Idle);
                    }
                } },
                {"Reinitialize", () => {
                    bool instateFlag = _commandLine.Switch("i") || _commandLine.Switch("instate");
                    InitializeBlocks(instateFlag);
                } },
                {"Help", () => {
                    //TEST
                    if (_commandLine.ArgumentCount > 1) {
                        string documentationEntry;
                        string potentialCommand = _commandLine.Argument(1);
                        if(dicCommandsDocumentation.TryGetValue(potentialCommand, out documentationEntry)) logger.AppendLine(documentationEntry);
                        else logger.AppendLine($"\"{potentialCommand}\" matches no valid command.");
                    }
                    else {
                        logger.AppendLine($"To learn more about a command, issue a \"Help <command>\" command. All commands are:");
                        foreach(string key in dicCommands.Keys) logger.messageBuilder.AppendLine($"    {key}");
                    }
                } },
                {"Instate", () => {
                    bool recommissionFlag = _commandLine.Switch("r") || _commandLine.Switch("recommission");
                    int argumentCount = _commandLine.ArgumentCount;
                    string[] constructsToInstate = new string[argumentCount-1];
                    for (int i = 1; i < argumentCount; i++) constructsToInstate[i-1] = _commandLine.Argument(i);
                    Instate(constructsToInstate, recommissionFlag);
                } },
                {"Decommission", () => {
                    int argumentCount = _commandLine.ArgumentCount;
                    string[] constructsToDecommission = new string[argumentCount-1];
                    for (int i = 1; i < argumentCount; i++) constructsToDecommission[i-1] = _commandLine.Argument(i);
                    Decommission(constructsToDecommission);
                } },
                {"Recommission", () => {
                    int argumentCount = _commandLine.ArgumentCount;
                    string[] constructsToRecommission = new string[argumentCount-1];
                    for (int i = 1; i < argumentCount; i++) constructsToRecommission[i-1] = _commandLine.Argument(i);
                    Decommission(constructsToRecommission, recommissionInstead: true);
                } },
                {"Status", () => {
                    //TODO: Log data about what parts of the solar orbit is/are missing!
                    string printableRoutine = currentRoutine == Routine.None ? "idling" : "managing all instating installations";
                    var solarInstallationsIDAndStatus = new List<string>();
                    foreach(SolarInstallation si in registeredInstallationsDic.Values) {
                        string currentStatus = SolarInstallation.printableStatus.ContainsKey(si.Status) ? SolarInstallation.printableStatus[si.Status] : si.Status.ToString();
                        solarInstallationsIDAndStatus.Add($"{si.ID}: {currentStatus}");
                    }
                    Func<bool, string> mappedString = isMapped => isMapped ? "available" : "missing";
                    string solarOrbitInfoStatus = "completely missing.";
                    string warningOrNot = "WARNING: ";
                    if (soInstance.IsMapped()) {
                        solarOrbitInfoStatus = "fully available.";
                        warningOrNot = "";
                    }
                    else if (soInstance.IsMapped(SunOrbit.DataPoint.PlaneNormal)) solarOrbitInfoStatus = "only partially available.";
                    logger.AppendLine($"I am currently {printableRoutine}.\n" +
                        $"{warningOrNot}Currently stored solar orbit info is {solarOrbitInfoStatus}.\n" +
                        $"Registered solar installations and their status are:\n    {string.Join("\n    ", solarInstallationsIDAndStatus)}");
                } },
                {"RequestData", () => RequestSunData() },
                {"PrintOrbit", () => {
                    //TEST
                    if(!soInstance.IsMapped(SunOrbit.DataPoint.PlaneNormal)) logger.AppendLine($"ERROR: No sufficient sun orbit data is available.");
                    else{
                        if(_commandLine.ArgumentCount > 1) PrintOrbit(_commandLine.Argument(1));
                        else logger.AppendLine("ERROR: No LCD panel specified to print onto.");
                    }
                } },
                {"ClearLog", () => {logger.Clear(); } },
                {"Debug", () => {
                    Echo($"{Runtime.MaxInstructionCount}");
                } },
            };
            #endregion
            #region Dictionary command documentation
            //TODO: Write documentation for all commands
            dicCommandsDocumentation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                {"Run", "Run [-instate | -i]\n" +
                "If solar orbit data is available, all solar installations marked for instatement are aligned to the sun. This process will take a while.\n" +
                "[-instate | -i] Perform an instate all (\"Instate *\") command before running."},

                {"Halt", "Halt\n" +
                "Cancels managing all instating solar installations and halts their rotation."},

                {"Reinitialize", "Reinitialize [-instate | -i]\n" +
                "Scans for new solar installations and log displays, unregisters nonexistent former solar installations and updates all already registered ones.\n" +
                "[-instate | -i] Perform an instate all (\"Instate *\") command after reinitializing."},

                {"Help", "Help <command>\n" +
                "Displays a list of all valid commands.\n" +
                "<command> OPTIONAL: Displays the documentation of the specified command instead."},

                {"Instate", "Instate <solar installation IDs> [-recommission | -r]\n" +
                "Marks specified, registered solar installations for instatement.\n" +
                "<solar installation IDs> The IDs of solar installations that ought to be handled by this command, seperated by spaces. If an ID contains any spaces, it must " +
                "be surrounded by quotation marks. If only an asterisk (*) or dot (.) is given, instead ALL registered solar installations are handled. IDs must be exact in capitalization.\n" +
                "E.g.: instate rooftop02 \"MY FIRST INSTALLATION\" backLandingPad -r\n" +
                "[-recommission | -r] Recommissions specified solar installations before attempting to mark them for instatement."},

                {"Decommission", "Decommission <solar installation IDs>\n" +
                "Decommissions specified, registered solar installations. Their movement is halted and are removed from instatement if applicable.\n" +
                "<solar installation IDs> The IDs of solar installations that ought to be handled by this command, seperated by spaces. If an ID contains any spaces, it must " +
                "be surrounded by quotation marks. If only an asterisk (*) or dot (.) are given, instead ALL registered solar installations are handled. IDs must be exact in capitalization.\n" +
                "E.g.: decommission rooftop02_OLD \"the fun one\""},

                {"Recommission", "Recommission <solar installation IDs>\n" +
                "Recommissions specified, registered solar installations.\n" +
                "<solar installation IDs> The IDs of solar installations that ought to be handled by this command, seperated by spaces. If an ID contains any spaces, it must " +
                "be surrounded by quotation marks. If only an asterisk (*) or dot (.) are given, instead ALL registered solar installations are handled. IDs must be exact in capitalization.\n" +
                "E.g.: recommission roofTop02_OLD \"MY FIRST INSTALLATION\""},

                {"Status", "Status\n" +
                "Displays information about what solar orbit data is available, what the Solar Installation Manager is currently doing and the status of all registered solar installations."},

                {"RequestData", "RequestData\n" +
                "Sends a signal out at maximum antenna strength requesting sun orbit data. If a Solar Analyzer or Solar Installation Manager caught the signal and has data stored, they will " +
                "send it to this PB and, if received, the data will be stored next tick."},

                {"PrintOrbit", "PrintOrbit <LCD panel name>\n" +
                "If at least partial solar orbit data is available, prints GPS coordinates that describe the sun's orbit onto the specified LCD panel.\n" +
                "<LCD panel name> Exact name of the LCD panel to print GPS coordinates onto. If multiple under this name exist, the first one found is used. " +
                "WARNING: All text on this panel will be erased and replaced."},

                {"ClearLog", "ClearLog\n" +
                "Clears the log of its data."},
            };
            #endregion

            #region INI reading
            _ini.TryParse(Storage);
            logger.ReadFromIni(_ini);
            soInstance.ReadFromIni(_ini);
            string[] formerlyRegisteredIDs = _ini.Get(INI_SECTION_NAME, INI_KEY_REGISTERED_IDs).ToString().Split(',');
            if(formerlyRegisteredIDs[0].Length > 0) foreach(string str in formerlyRegisteredIDs) registeredInstallationsDic.Add(str, null);
            currentRoutine = (Routine)_ini.Get(INI_SECTION_NAME, INI_KEY_CURRENT_ROUTINE).ToInt32();
            #endregion

            InitializeBlocks(false, calledFromConstructor: true);
            _ini.TryParse(Storage);
            foreach(SolarInstallation si in registeredInstallationsDic.Values) {
                si.ReadFromIni(_ini);
                if(si.HasWorkingStatus) instatingInstallationsSet.Add(si);
            }

            logger.WriteLogsToAllDisplays();
            logger.PrintMsgBuilder();
            ChangeCurrentRoutine(currentRoutine, runFromConstructor: true);
        }
        public void Save() {
            _ini.Clear();
            logger.WriteToIni(_ini);
            soInstance.WriteToIni(_ini);
            var registeredIDsList = new List<string>(registeredInstallationsDic.Keys);
            foreach(SolarInstallation si in registeredInstallationsDic.Values) si.WriteToIni(_ini);
            _ini.Set(INI_SECTION_NAME, INI_KEY_REGISTERED_IDs, string.Join(",", registeredIDsList));
            _ini.Set(INI_SECTION_NAME, INI_KEY_CURRENT_ROUTINE, (int)currentRoutine);

            Storage = _ini.ToString();
        }
        public void Main(string argument, UpdateType updateSource) {
            float tickDurationMs = 1000f / 60;
            float lastRunTimeMs = (float)Runtime.LastRunTimeMs;
            Echo($"LastRunTimeMs: {lastRunTimeMs}\nPercentage of a tick: {lastRunTimeMs / tickDurationMs * 100}%\nCurrent routine: {currentRoutine}");

            currentUpdateSource = updateSource;
            if((updateSource & (UpdateType.Trigger | UpdateType.Terminal)) != 0) {
                if(_commandLine.TryParse(argument)) {
                    Action currentCommand;
                    if(dicCommands.TryGetValue(_commandLine.Argument(0), out currentCommand)) {
                        logger.AppendLine($"Executing command line: {string.Join(" ", _commandLine.Items)}");
                        currentCommand();
                    }
                    else logger.AppendLine($"ERROR: No valid command was passed as an argument. Issue a \"Help\" command to see a list of available ones.");
                }
                else {
                    logger.AppendLine($"No valid argument was passed, executing the default Run command...");
                    dicCommands["Run"]();
                }
            }
            else if((updateSource & UpdateType.IGC) != 0) {
                logger.AppendLine(argument);
                //TODO: logging scenarios:
                //0, 1, 2, 3 data points NEWLY ADDED
                //0, 1, 2, 3 data points OVERRIDDEN
                //unicast received but it wasn't sun orbit related
                var currentPlaneNormal = soInstance.PlaneNormal;
                var currentRotationDirection = soInstance.RotationDirection;
                var currentAngularSpeed = soInstance.AngularSpeedRadPS;

                soInstance.IGC_ProcessMessages(); 
                foreach(SolarInstallation si in registeredInstallationsDic.Values) si.LocalizeSolarOrbitInfo(soInstance);
                //TODO: Log when messages are received and sent
                switch(argument) {
                    case SunOrbit.IGC_UNICAST_TAG:
                        logger.AppendLine("");
                        break;
                }
            }
            else dicRoutines[currentRoutine]();
            logger.PrintMsgBuilder();
        }
        #endregion
        #region Routine functions
        public void ChangeCurrentRoutine(Routine targetRoutine, bool runFromConstructor = false) {
            UpdateFrequency updateFrequency = UpdateFrequency.Update100;
            switch(targetRoutine) {
                case Routine.None:
                    updateFrequency = UpdateFrequency.None;
                    break;
                case Routine.ManageSolarInstallations:
                    if(runFromConstructor) Run();
                    break;
            }
            currentRoutine = targetRoutine;
            Runtime.UpdateFrequency = updateFrequency;
        }
        public void ManageActiveInstallations() {
            if(instatingInstallationsSet.Count == 0) {
                logger.AppendLine($"Finished instating all queued installations. Returning to idling...");
                ChangeCurrentRoutine(Routine.None);
            }
            foreach(SolarInstallation si in instatingInstallationsSet) si.ExecuteStatusRoutine(soInstance);
            //TEST: Performance, possibly optimize
            instatingInstallationsSet.RemoveWhere(si => !si.HasWorkingStatus);
        }
        #endregion
        #region Command functions
        public void Run(bool instateFlag = false) {
            if(instateFlag) Instate(new[] { "*" }, suppressLog: false);
            if(instatingInstallationsSet.Count == 0) {
                logger.AppendLine($"ERROR: Aborting Run command, as the instatement queue is empty.");
                return;
            }
            bool haveRegisteredConstructs = registeredInstallationsDic.Count > 0;
            bool haveAnySolarData = soInstance.IsMapped(SunOrbit.DataPoint.PlaneNormal);
            if(haveRegisteredConstructs && haveAnySolarData) {
                foreach(SolarInstallation targetInstatee in instatingInstallationsSet) {
                    SolarInstallation.SIStatus currentStatus = targetInstatee.Status;
                    SolarInstallation.SIStatus targetStatus = currentStatus == SolarInstallation.SIStatus.Idle ?
                        targetInstatee.IsPlaneNormalAligned ? SolarInstallation.SIStatus.AligningToSun : SolarInstallation.SIStatus.AligningToOrbitPlaneNormal :
                        currentStatus;
                    targetInstatee.ChangeStatus(targetStatus);
                }
                ChangeCurrentRoutine(Routine.ManageSolarInstallations);
                logger.AppendLine($"Now managing all solar installations marked for instatement...");
            }
            else {
                logger.AppendLine($"ERROR: Aborting Run command, as the following is unaddressed:");
                if(!haveRegisteredConstructs) logger.messageBuilder.AppendLine($"    - No constructs are registered.");
                if(!haveAnySolarData) logger.messageBuilder.AppendLine($"    - No sufficient sun orbit data is available.");
            }
        }
        public void Instate(string[] instateeIDs, bool recommissionFlag = false, bool suppressLog = false) {
            if(instateeIDs.Length == 0) {
                logger.AppendLine($"ERROR: No construct(s) specified to instate.");
                return;
            }
            if(!soInstance.IsMapped(SunOrbit.DataPoint.PlaneNormal)) {
                logger.AppendLine($"ERROR: Aborting Instate command, as no sun orbit data is availble");
                return;
            }
            var invalidConstructIDs = new List<string>();
            var alreadyInstatementMarkedIDs = new List<string>();
            var alreadyAlignedIDs = new List<string>();
            var decommissionedIDs = new List<string>();
            var needFurtherSunOrbitDataIDs = new List<string>();
            var newlyInstatedIDs = new List<string>();
            string[] constructIDs = instateeIDs[0] == "*" || instateeIDs[0] == "." ? registeredInstallationsDic.Keys.ToArray() : instateeIDs;
            foreach(string ID in constructIDs) {
                SolarInstallation targetInstatee;
                if(!registeredInstallationsDic.TryGetValue(ID, out targetInstatee)) {
                    invalidConstructIDs.Add(ID);
                    continue;
                }
                if(instatingInstallationsSet.Contains(targetInstatee)) {
                    alreadyInstatementMarkedIDs.Add(ID);
                    continue;
                }
                if(targetInstatee.Status == SolarInstallation.SIStatus.Aligned) {
                    alreadyAlignedIDs.Add(ID);
                    continue;
                }
                if(targetInstatee.Status == SolarInstallation.SIStatus.Decommissioned) {
                    if(recommissionFlag) Decommission(new string[] { ID }, true, true);
                    else {
                        decommissionedIDs.Add(ID);
                        continue;
                    }
                }
                if(targetInstatee.IsPlaneNormalAligned && !soInstance.IsMapped()) {
                    needFurtherSunOrbitDataIDs.Add(ID);
                    continue;
                }
                instatingInstallationsSet.Add(targetInstatee);
                newlyInstatedIDs.Add(ID);
            }
            if(suppressLog) return;
            if(invalidConstructIDs.Count > 0) {
                bool singular = invalidConstructIDs.Count == 1;
                logger.AppendLine($"ERROR: Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", invalidConstructIDs)}\" " +
                    $"{GrammaticalNumber("is", singular)} no registered {GrammaticalNumber("construct", singular)}.");
            }
            if(alreadyInstatementMarkedIDs.Count > 0) {
                bool singular = alreadyInstatementMarkedIDs.Count == 1;
                logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", alreadyInstatementMarkedIDs)}\" " +
                    $"{GrammaticalNumber("is", singular)} already marked for instatement.");
            }
            if(alreadyAlignedIDs.Count > 0) {
                bool singular = alreadyAlignedIDs.Count == 1;
                logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", alreadyAlignedIDs)}\" " +
                    $"{GrammaticalNumber("has", singular)} already been instated.");
            }
            if(decommissionedIDs.Count > 0) {
                bool singular = decommissionedIDs.Count == 1;
                logger.AppendLine($"Couldn't instate solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", decommissionedIDs)}\", as " +
                    $"{GrammaticalNumber("it", singular)} {GrammaticalNumber("is", singular)} decommissioned.");
            }
            if(needFurtherSunOrbitDataIDs.Count > 0) {
                bool singular = needFurtherSunOrbitDataIDs.Count == 1;
                logger.AppendLine($"Couldn't further instate solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", needFurtherSunOrbitDataIDs)}\", as " +
                    $"current sun orbit data is incomplete.");
            }
            if(newlyInstatedIDs.Count > 0) {
                bool singular = newlyInstatedIDs.Count == 1;
                logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", newlyInstatedIDs)}\" " +
                    $"{GrammaticalNumber("has", singular)} been newly marked for instatement.");
            }
        }
        public void Decommission(string[] decomissioneeIDs, bool recommissionInstead = false, bool suppressLog = false) {
            string[] deReCommission = !recommissionInstead ? new[] { "decommission", "decommissioned", "decommissioned" } : new[] { "recommission", "in commission", "recommissioned" };
            if(decomissioneeIDs.Length == 0) {
                logger.AppendLine($"ERROR: No construct(s) specified to {deReCommission[0]}.");
                return;
            }
            var invalidConstructIDs = new List<string>();
            var alreadyDecommissionedIDs = new List<string>();
            var newlyDecommissionedIDs = new List<string>();
            string[] constructIDs = decomissioneeIDs[0] == "*" || decomissioneeIDs[0] == "." ? registeredInstallationsDic.Keys.ToArray() : decomissioneeIDs;
            foreach(string ID in constructIDs) {
                SolarInstallation targetDecommissionee;
                if(!registeredInstallationsDic.TryGetValue(ID, out targetDecommissionee)) {
                    invalidConstructIDs.Add(ID);
                    continue;
                }
                bool isAlreadyDeReCommissioned = recommissionInstead ?
                    targetDecommissionee.Status != SolarInstallation.SIStatus.Decommissioned : targetDecommissionee.Status == SolarInstallation.SIStatus.Decommissioned;
                if(isAlreadyDeReCommissioned) {
                    alreadyDecommissionedIDs.Add(ID);
                    continue;
                }
                instatingInstallationsSet.Remove(targetDecommissionee);
                SolarInstallation.SIStatus targetStatus = recommissionInstead ? SolarInstallation.SIStatus.Idle : SolarInstallation.SIStatus.Decommissioned;
                targetDecommissionee.ChangeStatus(targetStatus);
                newlyDecommissionedIDs.Add(ID);
            }
            if(suppressLog) return;
            if(invalidConstructIDs.Count > 0) {
                bool singular = invalidConstructIDs.Count == 1;
                logger.AppendLine($"ERROR: Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", invalidConstructIDs)}\" " +
                    $"{GrammaticalNumber("is", singular)} no registered {GrammaticalNumber("construct", singular)}.");
            }
            if(alreadyDecommissionedIDs.Count > 0) {
                bool singular = alreadyDecommissionedIDs.Count == 1;
                logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", alreadyDecommissionedIDs)}\" " +
                    $"{GrammaticalNumber("is", singular)} already {deReCommission[1]}.");
            }
            if(newlyDecommissionedIDs.Count > 0) {
                bool singular = newlyDecommissionedIDs.Count == 1;
                logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", newlyDecommissionedIDs)}\" " +
                    $"{GrammaticalNumber("has", singular)} been {deReCommission[2]}.");
            }
        }
        public void RequestSunData() {
            //TODO: allow for explicit request from non analyzers OR edit the SunOrbit.request function to first IGC all analyzers, and if nothing was caught, ask all non-analyzers?
            //TODO: Give A LOT more feedback when: data is requested but none is received, messages are received, data is stored/modified
            soInstance.IGC_BroadcastRequestData(Me.CubeGrid.CustomName);
        }
        public void PrintOrbit(string textPanelName) {
            var textPanels = new List<IMyTextPanel>();
            GridTerminalSystem.GetBlocksOfType(textPanels, panel => panel.IsSameConstructAs(Me));
            foreach(var panel in textPanels)
                if(panel.CustomName == textPanelName) {
                    if(soInstance.PrintGPSCoordsRepresentingOrbit(panel)) logger.AppendLine($"Printed GPS coordinates describing the sun orbit onto LCD panel \"{textPanelName}\".");
                    return;
                }
            logger.AppendLine($"ERROR: No LCD panel of name \"{textPanelName}\" found.");
        }
        public void InitializeBlocks(bool instateFlag, bool calledFromConstructor = false) {
            Func<IMyTerminalBlock, bool> basePredicate = block => block.IsSameConstructAs(Me) && block.IsFunctional;
            Func<IMyTerminalBlock, Type, bool> isEqualBlockType = (block, type) => block.GetType().Name == type.Name.Substring(1);
            Action<IMyTerminalBlock, string> markAsErroredAndAppendLog = (block, message) => {
                block.ShowOnHUD = true;
                block.ShowInTerminal = true;
                if(!block.CustomName.StartsWith("[ERROR]")) block.CustomName = "[ERROR]" + block.CustomName;
                logger.AppendLine(message);
            };
            Me.CustomName = $"PB.{INI_SECTION_NAME}";
            logger.ScanGTSForLogDisplays(GridTerminalSystem);

            var rotorBaseList = new List<IMyMotorStator>();
            var nonHarvesterSIIDList = new List<string>();
            var unregisteredIDsSet = new HashSet<string>(registeredInstallationsDic.Keys);
            var newlyRegisteredIDsList = new List<string>();
            var harvesterCountUpdatedIDsList = new List<string>();
            var instateeIDList = new List<string>();
            GridTerminalSystem.GetBlocksOfType(rotorBaseList, block => MyIni.HasSection(block.CustomData, INI_SECTION_NAME) &&
                Rotor.IsMatchingMotorStatorSubtype(block) == true && basePredicate(block));
            foreach(IMyMotorStator potentialRotorBase in rotorBaseList) {
                MyIniParseResult parseResult;
                IMyMotorStator hingeBase;
                IMyMotorStator rotorTop;
                string logMessage;
                if(!_ini.TryParse(potentialRotorBase.CustomData, out parseResult)) {
                    logMessage = $"ERROR: Failed to parse custom data of rotor \"{potentialRotorBase.CustomName}\" on grid \"{potentialRotorBase.CubeGrid.CustomName}\":" +
                        $"\n{parseResult.Error}";
                    markAsErroredAndAppendLog(potentialRotorBase, logMessage);
                    continue;
                }
                string id = _ini.Get(INI_SECTION_NAME, "ID").ToString().Trim();
                if(!(id.Length > 0)) {
                    logMessage = $"ERROR: No ID key and/or value in custom data of rotor base \"{potentialRotorBase.CustomName}\" on grid \"{potentialRotorBase.CubeGrid.CustomName}\".";
                    markAsErroredAndAppendLog(potentialRotorBase, logMessage);
                    continue;
                }
                if(id == "*" || id == ".") {
                    logMessage = $"ERROR: ID values of \"*\" or \".\" are illegal, " +
                        $"present in custom data of rotor base \"{potentialRotorBase.CustomName}\" on grid \"{potentialRotorBase.CubeGrid.CustomName}\".";
                    markAsErroredAndAppendLog(potentialRotorBase, logMessage);
                    continue;
                }
                SolarInstallation possiblyExistentSI;
                if(registeredInstallationsDic.TryGetValue(id, out possiblyExistentSI) && possiblyExistentSI is object) {
                    if(potentialRotorBase != registeredInstallationsDic[id].rotorBase.terminalBlock) {
                        logMessage = $"ERROR: Rotor base \"{potentialRotorBase.CustomName}\" contains a non-unique ID. " +
                            $"\"{id}\" is already registered under rotor base \"{registeredInstallationsDic[id].rotorBase.terminalBlock.CustomName}\".";
                        markAsErroredAndAppendLog(potentialRotorBase, logMessage);
                        continue;
                    }
                    else if(possiblyExistentSI.IsValidStructure) {
                        unregisteredIDsSet.Remove(id);
                        var previousSolarPanelCount = possiblyExistentSI.SolarPanelCount;
                        var previousOxygenFarmcount = possiblyExistentSI.OxygenFarmCount;
                        possiblyExistentSI.UpdateHarvesterCounts(GridTerminalSystem);
                        if(!possiblyExistentSI.HasSolarHarvester) nonHarvesterSIIDList.Add(possiblyExistentSI.ID);
                        else if(previousSolarPanelCount != possiblyExistentSI.SolarPanelCount || previousOxygenFarmcount != possiblyExistentSI.OxygenFarmCount)
                            harvesterCountUpdatedIDsList.Add(possiblyExistentSI.ID);
                        continue;
                    }
                }
                hingeBase = Rotor.GetBlockOnTop(potentialRotorBase) as IMyMotorStator;
                if(!(hingeBase is object && Hinge.IsMatchingMotorStatorSubtype(hingeBase) == true)) {
                    logMessage = $"ERROR: No functional hinge detected on top of rotor base \"{potentialRotorBase.CustomName}\" in solar installation \"{id}\".";
                    markAsErroredAndAppendLog(potentialRotorBase, logMessage);
                    continue;
                }
                rotorTop = Hinge.GetBlockOnTop(hingeBase) as IMyMotorStator;
                if(!(rotorTop is object && Rotor.IsMatchingMotorStatorSubtype(rotorTop) == true && rotorTop.WorldMatrix.Up == new Hinge(hingeBase).HingeFacing)) {
                    logMessage = $"ERROR: No functional rotor detected on top of and pointing away from the hinge base in solar installation \"{id}\".";
                    markAsErroredAndAppendLog(potentialRotorBase, logMessage);
                    continue;
                }
                SolarInstallation newSI = new SolarInstallation(soInstance, potentialRotorBase, hingeBase, rotorTop, id, GridTerminalSystem);
                registeredInstallationsDic[id] = newSI;
                if(instateFlag) instateeIDList.Add(id);
                if(!unregisteredIDsSet.Remove(id)) newlyRegisteredIDsList.Add(id);
                if(!newSI.HasSolarHarvester) nonHarvesterSIIDList.Add(id);
            }
            if(unregisteredIDsSet.Count > 0) {
                foreach(string id in unregisteredIDsSet) registeredInstallationsDic.Remove(id);
                bool singular = unregisteredIDsSet.Count == 1;
                logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", unregisteredIDsSet)}\" " +
                    $"{GrammaticalNumber("has", singular)} been unregistered. Adios!");
            }
            if(newlyRegisteredIDsList.Count > 0) {
                bool singular = newlyRegisteredIDsList.Count == 1;
                logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", newlyRegisteredIDsList)}\" " +
                    $"{GrammaticalNumber("has", singular)} been registered.");
            }
            if(nonHarvesterSIIDList.Count > 0) {
                bool singular = nonHarvesterSIIDList.Count == 1;
                logger.AppendLine($"WARNING: Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", nonHarvesterSIIDList)}\" " +
                    $"{GrammaticalNumber("is", singular)} not capable of aligning to the sun, as no solar harvesters are installed on top.");
            }
            if(harvesterCountUpdatedIDsList.Count > 0) {
                bool singular = harvesterCountUpdatedIDsList.Count == 1;
                logger.AppendLine($"Solar {GrammaticalNumber("installation", singular)} \"{string.Join("\", \"", harvesterCountUpdatedIDsList)}\" " +
                    $"{GrammaticalNumber("has", singular)} had {GrammaticalNumber("its", singular)} solar harvester count updated.");
            }
            if(rotorBaseList.Count == 0 && _commandLine.ArgumentCount > 0)
                logger.AppendLine("No solar installation detected. A solar installation is a rotor on top of a hinge on top of a base rotor, where the " +
                    "base rotor has custom data with a solar installation section and an ID key and value. Capitalization only matters for the ID value. E.g.:\n" +
                    "[Solar Installation]\n" +
                    "ID = Roof Top02");
            if(instateeIDList.Count > 0) Instate(instateeIDList.ToArray());
            if(logger.messageBuilder.Length == 0 && !calledFromConstructor) logger.AppendLine("No changes have been recognized.");
        }
        #endregion
        readonly Dictionary<string, string> dicSingularPlural = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
            { "installation", "installations" },
            { "construct", "constructs" },
            { "is", "are" },
            { "has", "have" },
            { "it", "they" },
            { "its", "their" },
        };
        /// <param name="word">MUST be singular.</param>
        /// <returns>The singular or plural version of the input word.</returns>
        public string GrammaticalNumber(string word, bool singularElsePlural) {
            bool capitalize = char.IsUpper(word.ToCharArray()[0]);
            string returnStr = singularElsePlural ? word : dicSingularPlural[word];
            if(capitalize) {
                char[] letters = returnStr.ToCharArray();
                letters[0] = char.ToUpper(letters[0]);
                returnStr = new string(letters);
            }
            return returnStr;
        }
    }
}