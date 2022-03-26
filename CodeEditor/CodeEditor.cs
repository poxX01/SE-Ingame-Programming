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
        int ticksSinceLastReadoutChange;
        float oldestReadoutSinceLastChange;
        float previousOutput;
        float currentOutput;
        float[] deltas = new float[100];
        int indexToWriteTo;
        IMySolarPanel panel;
        public Program() {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(blocks);
            panel = GetBlock<IMySolarPanel>("", blocks);
            previousOutput = panel.MaxOutput;
            Me.CustomData = "";
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
        }
        public void Main(string argument, UpdateType updateSource) {
            Echo($"Performance: {Runtime.LastRunTimeMs / (1d / 60d * 1000d) * 100d}%\n");
            if((updateSource & UpdateType.Update1) != 0) {
                ticksSinceLastReadoutChange++;
                currentOutput = panel.MaxOutput;
                if(previousOutput != currentOutput && ticksSinceLastReadoutChange == 100) {
                    deltas[indexToWriteTo] = Math.Abs(currentOutput - oldestReadoutSinceLastChange);
                    indexToWriteTo++;
                    if(indexToWriteTo >= deltas.Length) {
                        Runtime.UpdateFrequency = UpdateFrequency.None;
                        return;
                    }
                    //Me.CustomData += $"delta: {Math.Abs(currentOutput - oldestReadoutSinceLastChange)} | ticks elapsed: {ticksSinceLastReadoutChange}\n";
                    ticksSinceLastReadoutChange = 0;
                    oldestReadoutSinceLastChange = currentOutput;
                }
                previousOutput = currentOutput;
            }
            else {
                if(indexToWriteTo >= deltas.Length) {
                    float total = 0;
                    for(int i = 0; i < deltas.Length; i++) {
                        total += deltas[i];
                    }
                    Me.CustomData = $"{total / deltas.Length}";
                }
            }
        }
        public T GetBlock<T>(string blockName = "", List<IMyTerminalBlock> blocks = null) {
            var blocksLocal = blocks ?? new List<IMyTerminalBlock>(); ;
            T myBlock = (T)GridTerminalSystem.GetBlockWithName(blockName);
            if(!(myBlock is object) && !(blocks is object)) GridTerminalSystem.GetBlocks(blocksLocal);
            if(!(myBlock is object)) myBlock = (T)blocksLocal.Find(block => block.GetType().Name == typeof(T).Name.Substring(1));
            if(myBlock is object) return myBlock;
            else throw new Exception($"An owned block of type {typeof(T).Name} does not exist in the provided block list.");
        }
        public void GenerateGPSCoordinate(Vector3D vector, string coordinateName) {
            Func<Vector3D, string, string, string> toGPSString = (vec, coordName, colorHex) => {
                vec.Normalize();
                vec *= Math.Pow(10, 12);
                return $"GPS:{coordName}:{vec.X}:{vec.Y}:{vec.Z}:{colorHex}:";
            };
            lcd.WriteText(toGPSString(vector * Math.Pow(10, 12), coordinateName, "#00FF16") + "\n", true);
        }
    }
}