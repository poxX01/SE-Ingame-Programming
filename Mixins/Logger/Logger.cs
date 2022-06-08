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
}
