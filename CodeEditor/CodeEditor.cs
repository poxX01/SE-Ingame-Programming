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
        const bool IS_SOLAR_ANALYZER = true;
        const bool CREATE_MOCK_DATA = true;

        MyCommandLine _commandLine = new MyCommandLine();
        public Program() {

        }
        enum Test { Zero, One, Two, Three }
        public void Main(string argument, UpdateType updateSource) {
            Test myEnum = (Test)5;
            Echo($"{Enum.IsDefined(typeof(Test), myEnum)}");
        }
        public T GetBlock<T>(string blockName = "", List<IMyTerminalBlock> blocks = null) {
            var blocksLocal = blocks ?? new List<IMyTerminalBlock>(); ;
            T myBlock = (T)GridTerminalSystem.GetBlockWithName(blockName);
            if(!(myBlock is object) && !(blocks is object)) GridTerminalSystem.GetBlocks(blocksLocal);
            if(!(myBlock is object)) myBlock = (T)blocksLocal.Find(block => block.GetType().Name == typeof(T).Name.Substring(1));
            if(myBlock is object) return myBlock;
            else throw new Exception($"An owned block of type {typeof(T).Name} does not exist in the provided block list.");
        }
        public void GenerateGPSCoordinateFromDirectionVector(Vector3D vector, string coordinateName, IMyTextSurface lcd) {
            Func<Vector3D, string, string, string> toGPSString = (vec, coordName, colorHex) => {
                vec.Normalize();
                vec *= Math.Pow(10, 12);
                return $"GPS:{coordName}:{vec.X}:{vec.Y}:{vec.Z}:{colorHex}:";
            };
            lcd.WriteText(toGPSString(vector * Math.Pow(10, 12), coordinateName, "#00FF16") + "\n", true);
        }
    }
}