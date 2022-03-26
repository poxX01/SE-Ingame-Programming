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

namespace IngameScript
{
    partial class Program : MyGridProgram
    {
        List<IMyCargoContainer> cargoContainersList;
        List<IMyTerminalBlock> refuelRequirersList;
        Type[] refuelRequirers;
        Func<IMyTerminalBlock, bool> IsRefuelRequirer;
        public Program()
        {
            Initialize();

        }
        public void Main(string argument, UpdateType updateSource)
        {

        }
        public void Initialize()
        {
            GridTerminalSystem.GetBlocksOfType(cargoContainersList, block => block.IsSameConstructAs(Me) && block.IsFunctional);
            GridTerminalSystem.GetBlocksOfType(refuelRequirersList, block => block.IsSameConstructAs(Me) && block.HasInventory && IsRefuelRequirer(block) && block.IsFunctional);

            IsRefuelRequirer = block => refuelRequirers.Contains(block.GetType());
            refuelRequirers = new Type[]{ typeof(IMyReactor), typeof(IMyAssembler), typeof(IMyRefinery), typeof(IMyGasGenerator) };
        }
        public void RequestDumpCargo()
        {

        }
    }
}
