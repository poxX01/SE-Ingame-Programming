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
        const string SECTION_NAME = "Ship Utilities";
        const UpdateType AUTOMATIC_UPDATE_TYPE = UpdateType.Update100 | UpdateType.Update10 | UpdateType.Update1 | UpdateType.Once;

        IMyShipController mainShipController;

        //Log() storage
        StringBuilder logger = new StringBuilder();
        IMyTextSurface lcdPB;
        IMyTextSurface lcdCockpit;

        //Dock() storage
        bool isDocked = false;

        //ToggleDocking storage
        List<IMyShipConnector> constructConnectors = new List<IMyShipConnector>();

        List<IMyTerminalBlock> constructTerminalBlocks = new List<IMyTerminalBlock>();
        List<IMyCargoContainer> cargoContainersList = new List<IMyCargoContainer>();
        List<IMyTerminalBlock> refuelRequirersList = new List<IMyTerminalBlock>();

        Dictionary<string, string> showInTerminalAfterUndockingTypeNames = new Dictionary<string, string>(); //First string is GetType().Name, second string is typeof(T).Name
        Routines currentRoutine = Routines.None;
        Routines routineToResumeOn = Routines.None; //Will resume on this routime after an intermediate routine
        MyCommandLine _commandLine = new MyCommandLine();
        MyIni _storageIni = new MyIni();
        Dictionary<Routines, Action> dicRoutines;
        Dictionary<string, Action> dicCommands;
        public class Logger{
            StringBuilder logger;
            List<IMyTextSurface> logTextSurfaces;
            List<IMyTextPanel> logLCDs;
            public void Log(string message) {
                message = $"[{DateTime.UtcNow}] " + message;
                if(!message.EndsWith("\n")) message += "\n";
                logger.Insert(0, message);
                foreach(IMyTextSurface textSurface in logTextSurfaces) textSurface.WriteText(logger);
            }
        }
        public enum Routines {
            None,
            Dock,
            AlignToDockingPlane,
        }
        public void ChangeCurrentRoutine(Routines targetRoutine) {
            UpdateFrequency updateFrequency;
            switch(targetRoutine) {
                case Routines.None:
                    updateFrequency = UpdateFrequency.None;
                    break;
                case Routines.AlignToDockingPlane:
                    updateFrequency = UpdateFrequency.Update1;
                    break;
                default:
                    updateFrequency = UpdateFrequency.Update100;
                    break;
            }
            currentRoutine = targetRoutine;
            Runtime.UpdateFrequency = updateFrequency;
        }
        public Program() {
            Type[] _refuelRequirersTypes = { typeof(IMyReactor), typeof(IMyAssembler), typeof(IMyRefinery), typeof(IMyGasGenerator) };
            Type[] _unhideInTerminalAfterUndockingTypes = {
                typeof(IMyCockpit),
                typeof(IMyRemoteControl),
                typeof(IMyProgrammableBlock),
                typeof(IMyTimerBlock),
                typeof(IMyRadioAntenna),
                typeof(IMyBeacon),
                typeof(IMyOreDetector)
            };
            foreach(Type type in _unhideInTerminalAfterUndockingTypes) showInTerminalAfterUndockingTypeNames.Add(type.Name, "I" + type.Name);
            InitializeBlocks();

            //Dock init
            foreach(IMyShipConnector connector in constructConnectors) {
                if(connector.Status == MyShipConnectorStatus.Connected) {
                    isDocked = true;
                    break;
                }
            }

            //Log() init
            lcdPB = Me.GetSurface(0);
            lcdPB.ReadText(logger);
            lcdPB.ContentType = ContentType.TEXT_AND_IMAGE;
            if(mainShipController is object && mainShipController.GetType().Name == typeof(IMyCockpit).Name.Substring(1)) {
                var cockpit = (IMyCockpit)mainShipController;
                lcdCockpit = cockpit.GetSurface(0);
                lcdCockpit.ReadText(logger);
                lcdCockpit.ContentType = ContentType.TEXT_AND_IMAGE;
            }

            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                {"Reinitialize", () => {
                    //TODO: Make this an enum for a more understandable user input and documentation effort
                    bool wipeAllCustomData = false;
                    bool.TryParse(_commandLine.Argument(1), out wipeAllCustomData);
                    InitializeBlocks(wipeAllCustomData);
                    } },
                {"Halt", () => ChangeCurrentRoutine(Routines.None) },
                {"Rename", () => Rename(_commandLine.Argument(1)) },
                {"ToggleDocking", () => ToggleDocking() },
            };
            dicRoutines = new Dictionary<Routines, Action>() {
                {Routines.None, () => { } },
                {Routines.Dock, () => Dock() },
                {Routines.AlignToDockingPlane, () => { } },
            };
        }
        public void Main(string argument, UpdateType updateSource) {
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
        #region Commands
        public void Rename(string targetName) {
            if(mainShipController is object) {
                if(!(targetName is object)) targetName = mainShipController.CubeGrid.CustomName;
                if(targetName.Contains('.')) {
                    Log($"ERROR: Character '.' is forbidden in a target prefix.");
                }
                else {
                    string prefix = "(LS)";
                    string customName;
                    if(mainShipController.CubeGrid.GridSizeEnum == MyCubeSize.Small) {
                        if(mainShipController.GetType().Name == typeof(IMyCockpit).Name.Substring(1)) prefix = "(SS)";
                        else prefix = "(RC)";
                    }
                    foreach(IMyTerminalBlock block in constructTerminalBlocks) {
                        customName = block.CustomName;
                        if(customName.StartsWith(prefix)) customName = customName.Substring(customName.IndexOf("." + 1));
                        block.CustomName = prefix + targetName + "." + customName;
                    }
                    mainShipController.CubeGrid.CustomName = targetName;
                }
            }
            else Log($"ERROR: No designated main ship controller (Cockpit, Remote Control or Cryopod with section [{SECTION_NAME}] in its custom data had been registered during initialization.");
        }
        public void ToggleDocking() {
            if(isDocked) {
                foreach(IMyShipConnector connector in constructConnectors) connector.Enabled = false;
                isDocked = !false;
            }
            else {
                foreach(IMyShipConnector connector in constructConnectors) {
                    connector.Enabled = true;
                    


                }
                ChangeCurrentRoutine(Routines.Dock);
            }
        }
        #endregion
        #region Routines
        public void Dock() {
            foreach(IMyShipConnector connector in constructConnectors) {
                connector.Connect();
                if(connector.Status == MyShipConnectorStatus.Connected) {
                    isDocked = true;
                    //constructTerminalBlocks.Sort(
                    break;
                }
            };
        }
        #endregion
        public void InitializeBlocks(bool wipeAllCustomData = false) {
            GridTerminalSystem.GetBlocksOfType(constructTerminalBlocks, block => block.IsSameConstructAs(Me));
            GridTerminalSystem.GetBlocksOfType(constructConnectors, block => block.IsSameConstructAs(Me));
            bool isDocked = constructConnectors.Exists(block => block.Status == MyShipConnectorStatus.Connected);

            List<IMyShipController> controllerList = new List<IMyShipController>();
            GridTerminalSystem.GetBlocksOfType(controllerList, block => block.IsSameConstructAs(Me));
            mainShipController = controllerList.Find(block => MyIni.HasSection(block.CustomData, SECTION_NAME)); //TODO: Overhaul this to check for keys

            MyIni customDataIni = new MyIni();
            customDataIni.AddSection(SECTION_NAME);
            foreach(IMyTerminalBlock block in constructTerminalBlocks) {
                bool showInTerminalAfterUndocking = showInTerminalAfterUndockingTypeNames.ContainsKey(block.GetType().Name);
                block.ShowInTerminal = !isDocked && showInTerminalAfterUndocking;
                if(wipeAllCustomData) block.CustomData = "";
                if(!MyIni.HasSection(block.CustomData, SECTION_NAME)) {
                    customDataIni.Set(SECTION_NAME, "showInTerminalAfterUndocking", showInTerminalAfterUndocking);
                    block.CustomData = customDataIni.ToString() + "\n" + block.CustomData;
                }
            }
        }
        public void Log(string message) {
            message = $"[{DateTime.UtcNow}] " + message;
            if(!message.EndsWith("\n")) message += "\n";
            logger.Insert(0, message);
            lcdPB.WriteText(logger.ToString());
            if(lcdCockpit is object) lcdCockpit.WriteText(logger.ToString());
        }
    }
}
