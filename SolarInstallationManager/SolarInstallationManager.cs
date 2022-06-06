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
using PXMixins_RotationHelper;
using PXMixins_PrincipleAxis;
using PXMixins_RotorAndHinge;
using PXMixins_SunOrbit;
using PXMixins_Logger

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
        const string NAME_PROGRAMMABLE_BLOCK = "Solar Installations Manager";
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
        readonly HashSet<SolarInstallation> maintainedInstallationsSet = new HashSet<SolarInstallation>();
        readonly RotationHelper rhInstance = new RotationHelper();
        readonly SunOrbit soInstance;

        readonly Logger logger;
        readonly MyIni _ini = new MyIni();
        readonly MyCommandLine _commandLine = new MyCommandLine();
        readonly Dictionary<Routine, Action> dicRoutines;
        readonly Dictionary<string, Action> dicCommands;
        public enum Routine { None, ManageSolarInstallations }
        public sealed class Logger {
            private const int MAX_CHARACTER_LIMIT = 100000;
            private const int MAX_LINE_LENGTH = 40;
            private const string INI_SUBSECTION_NAME = "Logger";
            private readonly string iniFullSectionName;
            private const string INI_KEY_LOG = "Log";
            private readonly StringBuilder log = new StringBuilder(100000);
            public readonly StringBuilder messageBuilder = new StringBuilder();
            public HashSet<IMyTextSurface> logDisplaySet = new HashSet<IMyTextSurface>();
            public Logger(IMyProgrammableBlock Me, string iniMainSectionName) {
                iniFullSectionName = $"{iniMainSectionName}.{INI_SUBSECTION_NAME}";
                AddDisplay(Me.GetSurface(0));
                //TODO:
            }
            public void PrintMsgBuilder() {
                string[] splitStrings = messageBuilder.ToString().Split('\n');
                messageBuilder.Clear();
                foreach(string str in splitStrings) {
                    if(str.Length > MAX_LINE_LENGTH) {
                        string[] strWords = str.Split(' ');
                        int counter = 0;
                        foreach(string word in strWords) {
                            counter += word.Length + 1;
                            if(counter > MAX_LINE_LENGTH) {
                                messageBuilder.Replace(' ', '\n', messageBuilder.Length - 1, 1);
                                counter = word.Length + 1;
                            }
                            messageBuilder.Append(word + ' ');
                        }
                        messageBuilder.Replace(' ', '\n', messageBuilder.Length - 1, 1);
                    }
                    else messageBuilder.AppendLine(str);
                }
                messageBuilder.Insert(0, $"[{DateTime.UtcNow}]\n");
                if(messageBuilder[messageBuilder.Length - 1] != '\n') messageBuilder.AppendLine();
                log.Insert(0, messageBuilder);
                if(log.Length > MAX_CHARACTER_LIMIT) log.Remove(MAX_CHARACTER_LIMIT, log.Length - MAX_CHARACTER_LIMIT);
                foreach(var lcd in logDisplaySet) lcd.WriteText(log);
                messageBuilder.Clear();
            }
            public void PrintString(string message) {
                messageBuilder.Append(message);
                PrintMsgBuilder();
            }
            public void AddDisplay(IMyTextSurface displayToAdd) {
                if(logDisplaySet.Add(displayToAdd)) {
                    displayToAdd.ContentType = ContentType.TEXT_AND_IMAGE;
                    displayToAdd.Alignment = TextAlignment.LEFT;
                    displayToAdd.Font = "Debug";
                    displayToAdd.FontSize = 0.7f;
                    displayToAdd.BackgroundColor = Color.Black;
                    displayToAdd.FontColor = Color.White;
                    displayToAdd.TextPadding = 2;
                    displayToAdd.ClearImagesFromSelection();
                }
            }
            public void RemoveDisplay(IMyTextSurface displayToRemove) {
                if(logDisplaySet.Remove(displayToRemove)) {
                    displayToRemove.ContentType = ContentType.NONE;
                    displayToRemove.FontSize = 1;
                    //TODO: use surfaceprovider as IMyTerminalBlock and textpanels instead, then wipe their custom data used to register
                    //      since custom data is used to mark one display to be shown for custom data
                }
            }
            public void WriteToIni(MyIni ini) {
                foreach(var display in logDisplaySet) { 
                    display.WriteText("");
                    display.ContentType = ContentType.NONE;
                }
                ini.Set(iniFullSectionName, INI_KEY_LOG, log.ToString());
            }
            public void ReadFromIni(MyIni ini) {
                if(ini.ContainsSection(iniFullSectionName)) {
                    log.Clear();
                    log.Append(ini.Get(iniFullSectionName, INI_KEY_LOG).ToString());
                }
            }
            public void Clear() {
                log.Clear();
                foreach(var display in logDisplaySet) display.WriteText(log);
            }
        }
        public sealed class SolarInstallation {
            private const float MASS_LARGE_SOLAR_PANEL = 416.8f; //in kg, vanilla is 416.8kg
            private const float MASS_SMALL_SOLAR_PANEL = 143.2f; //in kg, vanilla is 143.2kg
            private const float MASS_LARGE_OXYGEN_FARM = 3004; //in kg, vanilla is 3004
            private const float MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL = 0.16f; //in MW, vanilla is 0.16
            private const float MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL = 0.04f; //in MW, vanilla is 0.04
            private const float MAX_POSSIBLE_OUTPUT_LARGE_OXYGEN_FARM = 1; //Seems to be a percentage, 1 being the max 0.3L/s

            private const string INI_SECTION_NAME = "Solar Installation";

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
            public bool IsPlaneNormalAligned { get {
                    return rhLocal.IsAlignedWithNormalizedTargetVector(_targetOrbitPlaneNormal, hingeBase.HingeFacing);
                } 
            }
            private float MeasuredSunExposure { get {
                    if(refSolPanel is object) return refSolPanel.MaxOutput / _maxSolPanelOutput;
                    else return refOxyFarm.GetOutput() / MAX_POSSIBLE_OUTPUT_LARGE_OXYGEN_FARM;
                }
            }
            public string ID { get; }
            public SIStatus Status { get; private set; }
            public SolarInstallation(RotationHelper rhInstanceLocal, Rotor rotorBase, Hinge hingeBase, Rotor rotorTop,
                string id, IMyGridTerminalSystem GTS) {
                this.rotorBase = rotorBase;
                this.hingeBase = hingeBase;
                this.rotorTop = rotorTop;
                rhLocal = rhInstanceLocal;
                ID = id;
                UpdateHarvesterCounts(GTS);
            }
            public void UpdateHarvesterCounts(IMyGridTerminalSystem GTS) {
                float torque = 1000;
                var solarPanelList = new List<IMySolarPanel>();
                var oxygenFarmList = new List<IMyOxygenFarm>();
                GTS.GetBlocksOfType(solarPanelList, solPanel => solPanel.IsFunctional && solPanel.CubeGrid == rotorTop.terminalBlock.TopGrid);
                GTS.GetBlocksOfType(oxygenFarmList, oxyFarm => oxyFarm.IsFunctional && oxyFarm.CubeGrid == rotorTop.terminalBlock.TopGrid);
                SolarPanelCount = solarPanelList.Count;
                OxygenFarmCount = oxygenFarmList.Count;
                if(SolarPanelCount > 0) {
                    refSolPanel = solarPanelList.Find(panel => MyIni.HasSection(panel.CustomData, INI_SECTION_NAME)) ?? solarPanelList[0];
                    MyCubeSize gridSize = refSolPanel.CubeGrid.GridSizeEnum;
                    _maxSolPanelOutput = gridSize == MyCubeSize.Large ? MAX_POSSIBLE_OUTPUT_LARGE_SOLAR_PANEL : MAX_POSSIBLE_OUTPUT_SMALL_SOLAR_PANEL;
                    torque += SolarPanelCount * (gridSize == MyCubeSize.Large ? MASS_LARGE_SOLAR_PANEL : MASS_SMALL_SOLAR_PANEL);
                }
                if(OxygenFarmCount > 0) {
                    refOxyFarm = oxygenFarmList.Find(oxyFarm => MyIni.HasSection(oxyFarm.CustomData, INI_SECTION_NAME)) ?? oxygenFarmList[0];
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

            }
        }
        #region Constructor, Save & Main
        public Program() {
            Func<int, string[], string> invalidParameterMessage = (argIndex, validParams) =>
            $"Invalid parameter {inQuotes(_commandLine.Argument(argIndex))}. Valid parameters are:\n{string.Join(", ", validParams)}";

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
                {"Recommission", () => {
                    string targetInstallationID = _commandLine.Argument(1);
                    if (!registeredInstallationsDic.ContainsKey(targetInstallationID)) {
                        Log($"{NAME_SOLAR_INSTALLATION} {inQuotes(targetInstallationID)} is not a registered construct.\nReinitialize or correct for typos.");
                        return;
                    }
                    else {
                        registeredInstallationsDic[targetInstallationID].UpdateHarvesterCounts(GridTerminalSystem);
                    }
                } }, //TODO
                {"Decommission", () => {

                } }, //TODO
                {"ShowRegistered", () => {
                    StringBuilder message = new StringBuilder($"Currently performing routine {currentRoutine}.");
                    if (currentRoutine == Routine.AlignToSun){
                        message.Remove(message.Length-1, 1);
                        message.AppendLine(" on:");
                        foreach (var installation in aligningSolarInstallations) message.AppendLine(installation.id);
                    }
                    Log(message.ToString());
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
            ChangeCurrentRoutine(currentRoutine);
        }
        public void Save() {
            _ini.Clear();
            logger.WriteToIni(_ini);
            soInstance.WriteToIni(_ini);

            lcd.WriteText("");
            lcd.ContentType = ContentType.NONE;
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
            //TODO: Complete the code here, maybe revise on how solar installations are handled once aligned. Automatic checkup every couple of min? HasAligned --> removal from activeSolarInstallations Set?
            foreach(var si in maintainedInstallationsSet) {

            }
        }
        public void InitializeBlocks(bool calledInConstructor) {
            //TODO: Completely revamp this, checking if hinge and rotorTop are exactly on top of the connected rotorparts (via grid coords maybe?)
            //TODO: Add "potential unregistered solar installation" feature, that gets marked on hud and is then unmarked once setup
            //      (detects the rotor-hinge-rotor-solar harvester combo)
            //TODO: Allow for reference panel customization (search for custom data section first, in case one ought be specified, then possibly read custom max values)
            StringBuilder logMessage = new StringBuilder($"Finished initialization. Registered {NAME_SOLAR_INSTALLATION}s:\n");
            Func<IMyTerminalBlock, bool> basePredicate = block => block.IsSameConstructAs(Me) && block.IsFunctional;
            Func<IMyTerminalBlock, Type, bool> isEqualBlockType = (block, type) => block.GetType().Name == type.Name.Substring(1);
            Func<string, string> solarInstallationName = id => $"[{NAME_SOLAR_INSTALLATION_SHORTHAND}.{id}]";
            Action<IMyTerminalBlock> hideInTerminal = block => {
                block.ShowInTerminal = !HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL;
                block.ShowInToolbarConfig = !HIDE_SOLAR_INSTALLATION_BLOCKS_IN_TERMINAL;
            };

            lcd.ContentType = ContentType.TEXT_AND_IMAGE;
            Me.CustomName = $"PB.{INI_SECTION_NAME}";
            //TODO: Add LCD option as a log display

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
                    if(duplicate is object) { Log($"ERROR: Rotor {inQuotes(rotorBase.CustomName)} contains a non-unique ID.\nID {inQuotes(id)} already exists in Rotor {inQuotes(duplicate.rotorBase.terminalBlock.CustomName)}"); continue; }
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

                    SolarInstallation currentInstallation = new SolarInstallation(new RotationHelper(), rotorBase, hingeBase, rotorTop, id, allPanels.Count);
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