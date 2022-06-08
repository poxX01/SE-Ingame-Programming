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
        const string NAME_SOLAR_INSTALLATION = "Solar Installation";
        const string NAME_SOLAR_INSTALLATION_SHORTHAND = "SI";
        const string INI_SECTION_NAME = "Solar Installation Manager";
        const bool HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL = true;
        const bool SETUP_IMMEDIATELY_AFTER_REGISTRATION = true; //if true, upon successfully registering (initializing) a SolarInstallation, it will immediately set up its rotors' angles
        const bool ADD_TO_ALIGNMENT_CYCLE_UPON_SETUP = true; //if true, upon successfully initializing a SolarInstallation, it is immediately added to the alignment cycle
        const float ALIGNMENT_SUCCESS_THRESHOLD = 0.999995f;

        readonly Func<MyCubeSize, float> maxPossibleOutputMW = gridSize => gridSize == MyCubeSize.Small ? 0.04f : 0.16f; //in MW
        readonly Func<string, string[], bool> stringEqualsArrayElementIgnoreCase = (str, strArray) => {
            for(int i = 0; i < strArray.Length; i++) {
                if(str.Equals(strArray[i], StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }; //string.Contains(str, StringComparer) is prohibited in SE
        readonly Func<string, string> inQuotes = str => $"\"{str}\"";
        const UpdateType AUTOMATIC_UPDATE_TYPE = UpdateType.Update100 | UpdateType.Update10 | UpdateType.Update1 | UpdateType.Once;

        IMyTextSurface lcd;

        UpdateType currentUpdateSource;
        Routine currentRoutine;
        readonly Dictionary<string, SolarInstallation> registeredInstallationsDic = new Dictionary<string, SolarInstallation>();
        readonly HashSet<SolarInstallation> activeInstallationsSet = new HashSet<SolarInstallation>();
        readonly RotationHelper rhInstance = new RotationHelper();
        readonly SunOrbit soInstance;

        readonly Logger logger;
        readonly MyIni _ini = new MyIni();
        readonly MyCommandLine _commandLine = new MyCommandLine();
        readonly Dictionary<Routine, Action> dicRoutines;
        readonly Dictionary<string, Action> dicCommands;
        public enum Routine { None, ManageSolarInstallations }
        public sealed class SolarInstallation {
            private const float MASS_LARGE_SOLAR_PANEL = 416.8f; //in kg, vanilla is 416.8kg
            private const float MASS_SMALL_SOLAR_PANEL = 143.2f; //in kg, vanilla is 143.2kg
            private const float MASS_LARGE_OXYGEN_FARM = 3004; //in kg, vanilla is 3004
            private const float MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL = 0.16f; //in MW, vanilla is 0.16
            private const float MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL = 0.04f; //in MW, vanilla is 0.04
            private const float MAX_POSSIBLE_OUTPUT_LARGE_OXYGEN_FARM = 1; //Seems to be a percentage, 1 being the max 0.3L/s

            private const string INI_SECTION_PREFIX = "Solar Installation";
            private readonly string fullIniSectionName;
            private const string INI_KEY_STATUS = "Status";

            private float _maxSolPanelOutput;
            private float _maxOxyFarmOutput = 1;
            private float _sunExposureCache;
            private bool _isAligningWithCorrectVector;
            private Vector3D _targetOrbitPlaneNormal;
            private int _localRotationDirection;

            private int _routineCounter;
            public enum SIStatus { Idle, AligningToOrbitPlaneNormal, AligningToSun, MatchingSunRotation, Aligned }
            private IMySolarPanel refSolPanel;
            private IMyOxygenFarm refOxyFarm;
            public RotationHelper rhLocal;
            public readonly Rotor rotorBase;
            public readonly Hinge hingeBase;
            public readonly Rotor rotorTop;
            public int SolarPanelCount { get; private set; }
            public int OxygenFarmCount { get; private set; }
            private bool IsPlaneNormalAligned {
                get { return rhLocal.IsAlignedWithNormalizedTargetVector(_targetOrbitPlaneNormal, hingeBase.HingeFacing); }
            }
            private bool HasSolarHarvester {
                get { return refSolPanel is object || refOxyFarm is object; }
            }
            private bool IsSolarAlignmentCapable {
                get { return IsPlaneNormalAligned && HasSolarHarvester; }
            }
            private float MeasuredSunExposure {
                get {
                    if(refSolPanel is object) return refSolPanel.MaxOutput / _maxSolPanelOutput;
                    else return refOxyFarm.GetOutput() / MAX_POSSIBLE_OUTPUT_LARGE_OXYGEN_FARM;
                }
            }
            public string ID { get; }
            public SIStatus Status { get; private set; }
            public SolarInstallation(SunOrbit soInstance, Rotor rotorBase, Hinge hingeBase, Rotor rotorTop,
                string id, IMyGridTerminalSystem GTS) {
                this.rotorBase = rotorBase;
                this.hingeBase = hingeBase;
                this.rotorTop = rotorTop;
                rhLocal = new RotationHelper();
                ID = $"SI.{id}";
                fullIniSectionName = $"{INI_SECTION_PREFIX}.{id}";
                UpdateHarvesterCounts(GTS);
                LocalizeSolarOrbitInfo(soInstance);
            }
            public void UpdateHarvesterCounts(IMyGridTerminalSystem GTS) {
                float torque = 1000;
                var solarPanelList = new List<IMySolarPanel>();
                var oxygenFarmList = new List<IMyOxygenFarm>();
                GTS.GetBlocksOfType(solarPanelList, solPanel => solPanel.IsFunctional && solPanel.CubeGrid == rotorTop.terminalBlock.TopGrid);
                GTS.GetBlocksOfType(oxygenFarmList, oxyFarm => oxyFarm.IsFunctional && oxyFarm.CubeGrid == rotorTop.terminalBlock.TopGrid);
                SolarPanelCount = solarPanelList.Count;
                OxygenFarmCount = oxygenFarmList.Count;
                solarPanelList.ForEach(solPanel => solPanel.CustomName = $"[{ID}]Solar Panel");
                oxygenFarmList.ForEach(oxyFarm => oxyFarm.CustomName = $"[{ID}]Oxygen Farm");
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

                rotorTop.terminalBlock.Torque = torque; //TEST the torque relevancy
            }
            public void ChangeStatus(SIStatus targetStatus) {
                switch(targetStatus) {
                    case SIStatus.Idle:
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
                        //TODO: Trust the caller to assert AlignemtnCapability or do it ourselves via log pass?
                        _sunExposureCache = 0;
                        _routineCounter = 0;
                        rhLocal.ClearCache();
                        rotorTop.Unlock();
                        break;
                    case SIStatus.MatchingSunRotation:
                        rotorTop.Unlock();
                        break;
                }
                Status = targetStatus;
            }
            public void ExecuteStatusRoutine(SunOrbit soInstance, HashSet<SolarInstallation> routineExecutors) {
                switch(Status) {
                    case SIStatus.AligningToOrbitPlaneNormal:
                        if(IsPlaneNormalAligned) {
                            SIStatus targetStatus = soInstance.IsMapped() && HasSolarHarvester ? SIStatus.AligningToSun : SIStatus.Idle;
                            ChangeStatus(targetStatus);
                        }
                        break;
                    case SIStatus.AligningToSun:
                        AlignToSun();
                        break;
                    case SIStatus.MatchingSunRotation:
                        MatchSunRotation(soInstance);
                        break;
                    default:
                        routineExecutors.Remove(this);
                        break;
                }
            }
            public void MatchSunRotation(SunOrbit soInstance) {
                //TEST
                float currentSunExposure = MeasuredSunExposure;
                if(currentSunExposure < ALIGNMENT_SUCCESS_THRESHOLD) rotorTop.terminalBlock.TargetVelocityRad = (1 - currentSunExposure) * soInstance.AngularSpeedRadPS * _localRotationDirection;
                else ChangeStatus(SIStatus.Aligned);
            }
            public void AlignToSun() {
                //TEST
                float currentSunExposure = MeasuredSunExposure;
                if(currentSunExposure != 0) {
                    Vector3D alignmentVec = refSolPanel.WorldMatrix.Forward;
                    if(rhLocal.RotatedVectorClockwise == Vector3D.Zero) {
                        rhLocal.GenerateRotatedNormalizedVectorsAroundAxisByAngle(alignmentVec, _targetOrbitPlaneNormal, Math.Acos(currentSunExposure));
                        rotorBase.AlignToVector(rhLocal, alignmentVec, rhLocal.RotatedVectorClockwise);
                        _sunExposureCache = currentSunExposure;
                    }
                    else if(rhLocal.IsAlignedWithNormalizedTargetVector(rhLocal.RotatedVectorClockwise, alignmentVec)) {
                        if(currentSunExposure > _sunExposureCache) ChangeStatus(SIStatus.MatchingSunRotation);
                        else rotorBase.AlignToVector(rhLocal, alignmentVec, rhLocal.RotatedVectorCounterClockwise);
                    }
                    else if(rhLocal.IsAlignedWithNormalizedTargetVector(rhLocal.RotatedVectorCounterClockwise, alignmentVec)) {
                        if(currentSunExposure > _sunExposureCache) ChangeStatus(SIStatus.MatchingSunRotation);
                        else ChangeStatus(SIStatus.AligningToSun);
                    }
                }
            }
            public void LocalizeSolarOrbitInfo(SunOrbit soInstance) {
                if(soInstance.IsMapped(SunOrbit.DataPoint.PlaneNormal))
                    _targetOrbitPlaneNormal = rotorBase.LocalRotationAxis.Dot(soInstance.PlaneNormal) >= 0 ? soInstance.PlaneNormal : -soInstance.PlaneNormal;
                if(soInstance.IsMapped(SunOrbit.DataPoint.Direction))
                    _localRotationDirection = _targetOrbitPlaneNormal == soInstance.PlaneNormal ? soInstance.RotationDirection : -soInstance.RotationDirection;
            }
            public void WriteToIni(MyIni ini) {
                ini.Set(fullIniSectionName, INI_KEY_STATUS, (int)Status);
            }
            public void ReadFromIni(MyIni ini) {
                Status = (SIStatus)ini.Get(fullIniSectionName, INI_KEY_STATUS).ToInt32();
            }
        }
        #region Constructor, Save & Main
        public Program() {
            soInstance = new SunOrbit(IGC, false);
            logger = new Logger(Me, INI_SECTION_NAME);

            #region Dictionary Routines
            dicRoutines = new Dictionary<Routine, Action>() {
                {Routine.None, () => { } },
                {Routine.ManageSolarInstallations, () => { } }, //TODO
            };
            #endregion
            #region Dictionary commands
            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                //TODO: Allow for multi argument selection and one ALL operator '*', i.e. every argument after the 2nd is considered an SI, or umbrella them all under one line w/ a seperator e.g.
                //      "Rooftop installation, bot asteroid"
                {"Run", () => { ChangeCurrentRoutine(Routine.ManageSolarInstallations); } },
                {"Halt", () => ChangeCurrentRoutine(Routine.None) },
                {"Reinitialize", () => InitializeBlocks(false) },
                {"Activate", () => {
                    int argumentCount = _commandLine.ArgumentCount;
                    if (argumentCount < 2) { logger.PrintString("No construct(s) specified to activate."); return; }
                    List<string> targetSIIDList = new List<string>();
                    if (_commandLine.Argument(1) == "*") { foreach (string id in registeredInstallationsDic.Keys) targetSIIDList.Add(id); }
                    else { for (int i = 1; i < argumentCount; i++) targetSIIDList.Add(_commandLine.Argument(i)); }
                    foreach (string targetID in targetSIIDList){
                        if (registeredInstallationsDic.ContainsKey(targetID)){
                            SolarInstallation siToActivate = registeredInstallationsDic[targetID];
                            if (siToActivate.Status == SolarInstallation.SIStatus.Idle  && activeInstallationsSet.Add(siToActivate)){
                                registeredInstallationsDic[targetID].UpdateHarvesterCounts(GridTerminalSystem);
                                logger.messageBuilder.AppendLine($"{inQuotes(targetID)} has been activated.");
                            }
                            else logger.messageBuilder.AppendLine($"{inQuotes(targetID)} is already active.");
                        }
                        else logger.messageBuilder.AppendLine($"{inQuotes(targetID)} is not a registered construct.");
                    }
                    logger.PrintMsgBuilder();
                } }, //TODO
                {"Decommission", () => {

                } }, //TODO
                {"ShowRegistered", () => {
                    logger.messageBuilder.AppendLine($"Currently performing routine {currentRoutine}.\nRegistered solar installations and their status are:");
                    foreach(SolarInstallation si in registeredInstallationsDic.Values) logger.messageBuilder.AppendLine($"{si.ID}: {si.Status}");
                    logger.PrintMsgBuilder();
                } },
                {"ClearLog", () => {logger.Clear(); } },
            };
            #endregion
            #region INI reading
            _ini.TryParse(Storage);
            logger.ReadFromIni(_ini);
            soInstance.ReadFromIni(_ini);
            #endregion

            InitializeBlocks(true);
            foreach(SolarInstallation si in registeredInstallationsDic.Values) { 
                si.ReadFromIni(_ini);
                si.LocalizeSolarOrbitInfo(soInstance); 
            }
            ChangeCurrentRoutine(currentRoutine);
        }
        public void Save() {
            _ini.Clear();
            logger.WriteToIni(_ini);
            soInstance.WriteToIni(_ini);
            foreach (SolarInstallation si in registeredInstallationsDic.Values) si.WriteToIni(_ini);
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
                        logger.PrintString($"Executing command line: {string.Join(" ", _commandLine.Items)}");
                        currentCommand();
                    }
                    else {
                        logger.messageBuilder.AppendLine("ERROR: Invalid command was passed as an argument.\nValid commands are:\n");
                        foreach(string key in dicCommands.Keys) logger.messageBuilder.AppendLine(key);
                        logger.PrintMsgBuilder();
                    }
                }
                else dicCommands["Run"]();
            }
            else if((updateSource & UpdateType.IGC) != 0) {
                if(soInstance.IGC_ProcessMessages())
                    foreach(SolarInstallation si in registeredInstallationsDic.Values) si.LocalizeSolarOrbitInfo(soInstance);
            }
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
            if(!soInstance.IsMapped()) {
                Runtime.UpdateFrequency = UpdateFrequency.None;
                return;
            }
            foreach(SolarInstallation si in activeInstallationsSet) si.ExecuteStatusRoutine(soInstance, activeInstallationsSet);
        }
        public void InitializeBlocks(bool calledInConstructor) {
            //TODO: Completely revamp this, checking if hinge and rotorTop are exactly on top of the connected rotorparts (via grid coords maybe?)
            //TODO: Add "potential unregistered solar installation" feature, that gets marked on hud and is then unmarked once setup
            //      (detects the rotor-hinge-rotor-solar harvester combo)
            //TODO: Allow for reference panel customization (search for custom data section first, in case one ought be specified, then possibly read custom max values)
            Func<IMyTerminalBlock, bool> basePredicate = block => block.IsSameConstructAs(Me) && block.IsFunctional;
            Func<IMyTerminalBlock, Type, bool> isEqualBlockType = (block, type) => block.GetType().Name == type.Name.Substring(1);
            Func<string, string> solarInstallationNamePrefix = id => $"[{NAME_SOLAR_INSTALLATION_SHORTHAND}.{id}]";
            Action<IMyTerminalBlock> hideInTerminal = block => {
                block.ShowInTerminal = !HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL;
                block.ShowInToolbarConfig = !HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL;
                block.ShowOnHUD = false;
            };
            Action<IMyTerminalBlock, string> markAsErroredAndAppendLog = (block, message) => {
                block.ShowOnHUD = true;
                block.ShowInTerminal = true;
                if(!block.CustomName.StartsWith("[ERROR]")) block.CustomName = "[ERROR]" + block.CustomName;
                logger.messageBuilder.AppendLine(message);
            };

            logger.messageBuilder.AppendLine("Finished initialization. Registered solar installations:\n");
            Me.CustomName = $"PB.{INI_SECTION_NAME}";
            //TODO: Add LCD option as a log display

            var rotorBaseList = new List<IMyMotorStator>();
            GridTerminalSystem.GetBlocksOfType(rotorBaseList, block => MyIni.HasSection(block.CustomData, NAME_SOLAR_INSTALLATION) && basePredicate(block));
            //TODO: Use VectorI3 coordinates to determine whether things are in place, return feedback
            foreach(IMyMotorStator rotorBase in rotorBaseList) {
                MyIniParseResult parseResult;
                if(_ini.TryParse(rotorBase.CustomData, out parseResult)) {
                    IMyMotorStator hingeBase;
                    IMyMotorStator rotorTop;
                    string logMessage;
                    string id = _ini.Get(NAME_SOLAR_INSTALLATION, "ID").ToString();
                    if(!(id.Length > 0)) {
                        logMessage = $"ERROR: No ID key and/or value in custom data of rotor base {inQuotes(rotorBase.CustomName)} on grid {inQuotes(rotorBase.CubeGrid.CustomName)}.";
                        markAsErroredAndAppendLog(rotorBase, logMessage);
                        continue;
                    }
                    if(registeredInstallationsDic.ContainsKey(id)) {
                        if(rotorBase.EntityId != registeredInstallationsDic[id].rotorBase.terminalBlock.EntityId) {
                            logMessage = $"ERROR: Rotor base {inQuotes(rotorBase.CustomName)} contains a non-unique ID.\n" +
                                $"ID {inQuotes(id)} already exists in rotor base {inQuotes(registeredInstallationsDic[id].rotorBase.terminalBlock.CustomName)}";
                            markAsErroredAndAppendLog(rotorBase, logMessage);
                        }
                        continue;
                    }
                    
                }
                else {
                    logger.messageBuilder.AppendLine($"ERROR: Failed to parse custom data of rotor {inQuotes(rotorBase.CustomName)} " +
                        $"on grid {inQuotes(rotorBase.CubeGrid.CustomName)}:\n{parseResult.Error}");
                    continue;
                }
            }
            logger.PrintMsgBuilder();


            foreach(IMyMotorStator rotorBase in rotorBaseList) {
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
                    if(duplicate is object) { Log($"ERROR: Rotor {inQuotes(rotorBase.CustomName)} contains a non-unique ID.\nID {inQuotes(id)} already exists in Rotor {inQuotes(duplicate.rotorBase.terminalBlock.CustomName)}"); continue; }
                    if(hingeBase is object) {
                        var tempList = new List<IMyShipController>(1);
                        GridTerminalSystem.GetBlocksOfType(allPanels, panel => panel.CubeGrid == hingeBase.TopGrid && basePredicate(panel));
                        GridTerminalSystem.GetBlocksOfType(tempList, block => block.CubeGrid == hingeBase.TopGrid && basePredicate(block));
                    }
                    else { Log($"ERROR: Failed to find an owned, functional hinge on grid connected to rotor {inQuotes(rotorBase.CustomName)} in {solarInstallationNamePrefix(id)}."); continue; }
                    if(allPanels.Count > 0) {
                        referencePanel = allPanels.ElementAt(0);
                    }
                    else { Log($"ERROR: Failed to find owned, functional solar panels on grid connected to hinge {inQuotes(hingeBase.CustomName)} in {solarInstallationNamePrefix(id)}"); continue; }
                    foreach(IMySolarPanel panel in allPanels) {
                        hideInTerminal(panel);
                        panel.CustomName = $"Solar Panel.{solarInstallationNamePrefix(id)}";
                    }
                    hideInTerminal(hingeBase);
                    hideInTerminal(rotorBase);
                    referencePanel.CustomName = $"Solar Panel.Reference.{solarInstallationNamePrefix(id)}";
                    hingeBase.CustomName = $"Hinge.{solarInstallationNamePrefix(id)}";
                    rotorBase.CustomName = $"Rotor.{solarInstallationNamePrefix(id)}";
                    hingeBase.TopGrid.CustomName = $"{solarInstallationNamePrefix(id)}.Solar Array";
                    rotorBase.TopGrid.CustomName = $"{solarInstallationNamePrefix(id)}.RotorBase to HingeBase Connection";
                    float maxReferencePanelOutput = maxPossibleOutputMW(referencePanel.CubeGrid.GridSizeEnum);

                    SolarInstallation currentInstallation = new SolarInstallation(soInstance, rotorBase, hingeBase, rotorTop, id, GridTerminalSystem);
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
    }
}