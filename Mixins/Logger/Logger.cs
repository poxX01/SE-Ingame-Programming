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
    public sealed class LogDisplay {
        public readonly IMyTerminalBlock owningBlock;
        public readonly IMyTextSurface surface;
        public readonly float screenWidth;
        public LogDisplay(IMyTerminalBlock owningBlock, IMyTextSurface surface) {
            this.owningBlock = owningBlock;
            this.surface = surface;
            screenWidth = surface.SurfaceSize.X - 10;
        }
    }
    //TODO: Store a general, non wrapped log so that when a new display is added whose screenWidth has no log stored yet, we can create a newly wrapped log.
    public sealed class Logger {
        private const int MAX_CHARACTER_LIMIT = 100000;
        private const string INI_SECTION_NAME = "Logger";
        private readonly string iniFullSectionName;
        //The float stores the screen's width as defined in the LogDisplay class. Auto wrap varies based on it
        private readonly Dictionary<float, StringBuilder> formattedLogsDictionary = new Dictionary<float, StringBuilder>();
        private readonly HashSet<LogDisplay> logDisplaySet = new HashSet<LogDisplay>();
        private readonly StringBuilder workerSB = new StringBuilder();
        private readonly StringBuilder mainUnwrappedLog = new StringBuilder();
        private readonly MyIni _ini = new MyIni();

        public readonly StringBuilder messageBuilder = new StringBuilder();
        public Logger(IMyProgrammableBlock Me, string iniMainSectionName, IMyGridTerminalSystem GTS) {
            iniFullSectionName = $"{iniMainSectionName}.{INI_SECTION_NAME}";
            AddDisplay(new LogDisplay(Me, Me.GetSurface(0)));
            ScanGTSForLogDisplays(GTS);
        }
        public void ScanGTSForLogDisplays(IMyGridTerminalSystem GTS) {
            var candidateList = new List<IMyTerminalBlock>();
            GTS.GetBlocksOfType(candidateList, block => MyIni.HasSection(block.CustomData, iniFullSectionName));
            foreach(var block in candidateList) {
                IMyTextPanel textPanel = block as IMyTextPanel;
                IMyTextSurfaceProvider surfaceProvider = block as IMyTextSurfaceProvider;
                if(textPanel is object) AddDisplay(new LogDisplay(block, textPanel));
                else if(surfaceProvider is object) {
                    _ini.Clear();
                    int targetScreen = -1;
                    if (_ini.TryParse(block.CustomData)) targetScreen = _ini.Get(iniFullSectionName, "surface").ToInt32();
                    if(targetScreen <= surfaceProvider.SurfaceCount && targetScreen >= 0) AddDisplay(new LogDisplay(block, surfaceProvider.GetSurface(targetScreen)));
                }
            }
        }
        #region PrintLog functions
        public void WriteLogsToAllDisplays() {
            foreach(LogDisplay display in logDisplaySet) display.surface.WriteText(formattedLogsDictionary[display.screenWidth]);
        }
        public void WrapText(StringBuilder sbToAppendWrappedTextOnto, string[] inputLines, IMyTextSurface tsInstance, float screenWidth, string font = "Debug", float fontSize = 0.7f) {
            foreach(string str in inputLines) {
                workerSB.Append(str);
                if(tsInstance.MeasureStringInPixels(workerSB, font, fontSize).X > screenWidth) {
                    workerSB.Clear();
                    string[] strWords = str.Split(' ');
                    foreach(string word in strWords) {
                        workerSB.Append(word + ' ');
                        if(tsInstance.MeasureStringInPixels(workerSB, font, fontSize).X > screenWidth) {
                            workerSB.Length = workerSB.Length - (word.Length + 2);
                            sbToAppendWrappedTextOnto.AppendLine(workerSB.ToString());
                            workerSB.Clear();
                            workerSB.Append(word + ' ');
                        }
                    }
                    sbToAppendWrappedTextOnto.Append(workerSB);
                    sbToAppendWrappedTextOnto.Replace(' ', '\n', sbToAppendWrappedTextOnto.Length - 1, 1);
                }
                else sbToAppendWrappedTextOnto.AppendLine(str);
                workerSB.Clear();
            }
        }
        public void PrintMsgBuilder() {
            //TODO: Logger sometimes just collapses on itsself (maybe this is when the length gets cut?)
            if(messageBuilder.Length == 0) return;
            IMyTextSurface textSurfaceInstance = logDisplaySet.First().surface;
            string[] splitStrings = messageBuilder.ToString().Split('\n');
            messageBuilder.Clear();
            foreach(var dicEntry in formattedLogsDictionary) {
                StringBuilder log = dicEntry.Value;
                WrapText(messageBuilder, splitStrings, textSurfaceInstance, dicEntry.Key);
                messageBuilder.Insert(0, $"[{DateTime.UtcNow} UTC]\n");
                if(messageBuilder[messageBuilder.Length - 1] != '\n') messageBuilder.AppendLine();
                if(log.Length + messageBuilder.Length > MAX_CHARACTER_LIMIT) log.Length = MAX_CHARACTER_LIMIT / 10 * 9;
                log.Insert(0, messageBuilder);
                messageBuilder.Clear();
            }
            WriteLogsToAllDisplays();
            messageBuilder.Clear();
        }
        public void AppendLine(string message) {
            messageBuilder.AppendLine('>' + message);
        }
        #endregion
        #region Add & remove display functions
        public void AddDisplay(LogDisplay displayToAdd) {
            if(logDisplaySet.Add(displayToAdd)) {
                displayToAdd.surface.ContentType = ContentType.TEXT_AND_IMAGE;
                displayToAdd.surface.Alignment = TextAlignment.LEFT;
                displayToAdd.surface.Font = "Debug";
                displayToAdd.surface.FontSize = 0.7f;
                displayToAdd.surface.BackgroundColor = Color.Black;
                displayToAdd.surface.FontColor = Color.White;
                displayToAdd.surface.TextPadding = 2;
                displayToAdd.surface.ClearImagesFromSelection();
                if(!formattedLogsDictionary.ContainsKey(displayToAdd.screenWidth)) formattedLogsDictionary.Add(displayToAdd.screenWidth, new StringBuilder());
            }
        }
        public void RemoveDisplay(LogDisplay displayToRemove) {
            if(logDisplaySet.Remove(displayToRemove)) {
                displayToRemove.surface.ContentType = ContentType.NONE;
                displayToRemove.surface.FontSize = 1;
                _ini.Clear();
                if(_ini.TryParse(displayToRemove.owningBlock.CustomData)) { 
                    _ini.DeleteSection(iniFullSectionName);
                    displayToRemove.owningBlock.CustomData = _ini.ToString();
                }
                bool duplicateScreenWidthFound = false;
                foreach(LogDisplay persistingDisplay in logDisplaySet) {
                    duplicateScreenWidthFound = persistingDisplay.screenWidth == displayToRemove.screenWidth;
                    if(duplicateScreenWidthFound) break; 
                }
                if(!duplicateScreenWidthFound) formattedLogsDictionary.Remove(displayToRemove.screenWidth);
            }
        }
        #endregion
        #region INI write & read
        public void WriteToIni(MyIni ini) {
            foreach(var entry in formattedLogsDictionary) ini.Set(INI_SECTION_NAME, entry.Key.ToString(), entry.Value.Replace('\n', '`').ToString());
        }
        public void ReadFromIni(MyIni ini) {
            if(ini.ContainsSection(INI_SECTION_NAME)) {
                var iniKeys = new List<MyIniKey>();
                ini.GetKeys(INI_SECTION_NAME, iniKeys);
                formattedLogsDictionary.Clear();
                foreach(MyIniKey key in iniKeys) {
                    float screenWidth = float.Parse(key.Name);
                    StringBuilder sbValue = new StringBuilder(ini.Get(key).ToString());
                    formattedLogsDictionary.Add(screenWidth, sbValue.Replace('`', '\n'));
                }
            }
        }
        #endregion
        public void Clear() {
            foreach(StringBuilder log in formattedLogsDictionary.Values) log.Clear();
            foreach(LogDisplay display in logDisplaySet) display.surface.WriteText("");
        }
    }
}
