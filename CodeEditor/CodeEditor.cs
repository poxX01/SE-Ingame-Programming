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
        Rotor rotor;
        Hinge hinge;
        public Program() {
            rotor = new Rotor(GetBlock<IMyMotorStator>());
            hinge = new Hinge((IMyMotorStator)GridTerminalSystem.GetBlockWithId(rotor.terminalBlock.TopGrid.GetCubeBlock(rotor.BlockPositionOnTop).FatBlock.EntityId));
        }
        public void Main(string argument, UpdateType updateSource) {
            Echo($"{rotor.terminalBlock.Top.WorldMatrix.Up.Dot(rotor.terminalBlock.WorldMatrix.Up)}\n" +
                $"{hinge.HingeFacing.Dot(hinge.terminalBlock.WorldMatrix.Up)}");
        }
        public string ToGPSString(Vector3D vec, string coordName, string colorHex) {
            vec.Normalize();
            vec *= Math.Pow(10, 12);
            return $"GPS:{coordName}:{vec.X}:{vec.Y}:{vec.Z}:{colorHex}:\n";
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