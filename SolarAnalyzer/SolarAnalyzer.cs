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
        //Leave empty to have the program use the first functional one found on the same construct
        const string NAME_REFERENCE_SOLAR_PANEL = "";
        const string NAME_LCD = "";
        const string INI_SECTION_NAME = "SolarAnalyzer";
        const string INI_KEY_CURRENT_ROUTINE = "currentRoutine";
        const string INI_KEY_ROUTINE_TO_RESUME_ON = "routineToResumeOn";
        const string INI_KEY_BUFFER_VECTOR = "bufferVector";
        const string INI_KEY_FLOAT_LIST_AVERAGE = "floatList.Average()";
        const string INI_KEY_FLOAT_LIST_COUNT = "floatList.Count";

        //TODO: LOW PRIORITY: Support a large panel and modded ones (ratios may be different, especially with rounding throughout code)
        //E.g. introduce (in custom data) an entry for panel max output, however we'd need to check the rounding done in DetermineSunPlaneNormalManual
        //const float PRECISION_THRESHOLD = 0.999985f; //150W off perfect readout precision in a small panel
        const float PRECISION_THRESHOLD = 1;
        const float ALIGN_TO_SUN_OFFSET_THRESHOLD = PRECISION_THRESHOLD - 0.001f; //Any exposure value between 1 and this is considered aligned for that routine.
        const int MEASUREMENTS_TARGET_AMOUNT_SUN_DIRECTION = 8;
        const int MEASUREMENTS_TARGET_AMOUNT_SUN_SPEED = 2500;
        const UpdateType AUTOMATIC_UPDATE_TYPE = UpdateType.Update100 | UpdateType.Update10 | UpdateType.Update1 | UpdateType.Once;
        float maxPossibleOutput; //in MW

        int currentRoutineRepeats;
        int targetRoutineRepeats; //for Pause()
        float previousSunExposure;
        float mostRecentSunExposure; //1 for perfect exposure, 0 for absolutely no sun on our reference panel
        float exposureDelta;

        //Storage for DetermineSunPlaneNormal()
        const float SUN_EXPOSURE_PAUSE_THRESHOLD = -0.2f; //if exposure delta ever goes below this value in one run, Pause() is called
        PrincipalAxis currentRotationAxis = PrincipalAxis.Pitch;
        bool haveNotInvertedYetThisAxis = true;
        bool exposureDeltaGreaterThanZeroOnLastRun = false;
        int[] currentSigns = { 1, 1 };

        Vector3D bufferVector = new Vector3D(); //used in AlignForwardToSun() and DetermineSunPlaneNormals
        readonly List<float> floatList = new List<float>(MEASUREMENTS_TARGET_AMOUNT_SUN_SPEED); //for measurements in ObtainPreliminaryAngularSpeed and DetermineSunOrbitDirection
        readonly List<float> exposureDeltasList = new List<float>(); //TODO: Remove once Routines.Debug gets removed!

        //Storage for MANUAL LCD feedback
        readonly StringBuilder lcdText = new StringBuilder($"Align towards the sun within 1 and target margin.\n\nTarget: {PRECISION_THRESHOLD}\nCurrent: ");
        readonly int lcdTextDefaultLength;
        IMyTextPanel lcd;

        IMySolarPanel referenceSolarPanel;
        UpdateType currentUpdateSource;
        Routine currentRoutine;
        Routine routineToResumeOn; //Will resume on this routime after an intermediate routine (e.g. Pause and after AlignmentRoutines)
        readonly SunOrbit sunOrbit;
        readonly List<Gyroscope> registeredGyros = new List<Gyroscope>();
        readonly RotationHelper rotationHelper = new RotationHelper();
        readonly MyIni _ini = new MyIni();
        readonly MyCommandLine _commandLine = new MyCommandLine();
        readonly Dictionary<Routine, Action> dicRoutines;
        readonly Dictionary<string, Action> dicCommands;
        public enum Routine {
            None,
            Pause,
            DetermineSunPlaneNormal,
            DetermineSunPlaneNormalManual,
            AlignPanelDownToSunPlaneNormal,
            AlignPanelToSun,
            DetermineSunOrbitDirection,
            DetermineSunAngularSpeed,
            Debug
        }
        public Program() {
            InitializeBlocks();
            sunOrbit = new SunOrbit(IGC, true);
            #region ReadFromIni
            _ini.TryParse(Storage);
            currentRoutine = (Routine)_ini.Get(INI_SECTION_NAME, INI_KEY_CURRENT_ROUTINE).ToInt32();
            routineToResumeOn = (Routine)_ini.Get(INI_SECTION_NAME, INI_KEY_ROUTINE_TO_RESUME_ON).ToInt32();
            Vector3D.TryParse(_ini.Get(INI_SECTION_NAME, INI_KEY_BUFFER_VECTOR).ToString(), out bufferVector);
            if(currentRoutine == Routine.DetermineSunAngularSpeed) {
                float storedFloatListAverage = _ini.Get(INI_SECTION_NAME, INI_KEY_FLOAT_LIST_AVERAGE).ToSingle();
                int storedFloatListCount = _ini.Get(INI_SECTION_NAME, INI_KEY_FLOAT_LIST_COUNT).ToInt32();
                for(int i = 0; i < storedFloatListCount; i++) floatList.Add(storedFloatListAverage);
            }
            sunOrbit.ReadFromIni(_ini);
            #endregion
            #region Dictionary routines
            dicRoutines = new Dictionary<Routine, Action>() {
                {Routine.None, () => { } },
                {Routine.Pause, () => {
                    if(currentRoutineRepeats == targetRoutineRepeats) ChangeCurrentRoutine(routineToResumeOn);
                    else currentRoutineRepeats++;
                } },
                {Routine.DetermineSunPlaneNormal, () => DetermineSunPlaneNormal()},
                {Routine.DetermineSunPlaneNormalManual, () => DetermineSunPlaneNormalManual(lcd)},
                {Routine.AlignPanelDownToSunPlaneNormal, () => {
                    if(Gyroscope.AlignToTargetNormalizedVector(sunOrbit.PlaneNormal, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros) &&
                    (currentUpdateSource & UpdateType.Update100) != 0)
                        ChangeCurrentRoutine(Routine.AlignPanelToSun);
                }},
                {Routine.AlignPanelToSun, () => {
                    Gyroscope.AlignToTargetNormalizedVector(bufferVector, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros);
                    if((currentUpdateSource & UpdateType.Update100) != 0) {
                        UpdateExposureValues();
                        if (mostRecentSunExposure > ALIGN_TO_SUN_OFFSET_THRESHOLD) {ChangeCurrentRoutine(routineToResumeOn); return; }
                        else if (exposureDelta < 0) bufferVector = rotationHelper.RotatedVectorCounterClockwise;
                        if (rotationHelper.IsAlignedWithNormalizedTargetVector(bufferVector, referenceSolarPanel.WorldMatrix.Forward)) {
                            ChangeCurrentRoutine(Routine.AlignPanelDownToSunPlaneNormal);
                        }
                    }
                }},
                {Routine.DetermineSunOrbitDirection, () => DetermineSunOrbitDirection() },
                {Routine.DetermineSunAngularSpeed, () => DetermineSunAngularSpeed()},
                {Routine.Debug, () => {
                    UpdateExposureValues();
                    if (currentRoutineRepeats > 0) exposureDeltasList.Add(exposureDelta);
                    currentRoutineRepeats++;
                    lcd.WriteText($"mostRecentExposure: {mostRecentSunExposure}\npreviousExposure: {previousSunExposure}\nexposureDelta: {exposureDelta}\n" +
                        $"preliminaryAngularSpeed: {sunOrbit.AngularSpeedRadPS}\nexposureDeltaAveragesCount: {exposureDeltasList.Count}\n" +
                        $"exposureDeltaAverage: {(exposureDeltasList.Count > 0 ? exposureDeltasList.Average() : 0)}\n");
                }},
            };
            #endregion
            #region Dictionary commands
            dicCommands = new Dictionary<string, Action>(StringComparer.OrdinalIgnoreCase) {
                {"Run", () => {if (!sunOrbit.IsMapped()) ChangeCurrentRoutine(NextRoutineToMapSunOrbit()); } },
                {"Halt", () => ChangeCurrentRoutine(Routine.None) },
                {"Reinitialize", () => InitializeBlocks() },
                {"ClearData", () => {
                    SunOrbit.DataPoint dataPoint;
                    Enum.TryParse(_commandLine.Argument(1), out dataPoint);
                    sunOrbit.ClearData(dataPoint);
                } },
                {"PrintData", () => Me.CustomData = sunOrbit.PrintableDataPoints() },
                {"SendOverride", () => sunOrbit.IGC_BroadcastOverrideData() },
                {"AlignToSun", () => {routineToResumeOn = Routine.None; ChangeCurrentRoutine(Routine.AlignPanelDownToSunPlaneNormal); } },
                {"Debug", () => {
                    routineToResumeOn = Routine.Debug;
                    ChangeCurrentRoutine(Routine.AlignPanelDownToSunPlaneNormal);
                } },
            };
            #endregion
            lcdTextDefaultLength = lcdText.Length;
            lcd.ContentType = ContentType.TEXT_AND_IMAGE;
            ChangeCurrentRoutine(currentRoutine);
        }
        public void Save() {
            _ini.Clear();
            _ini.Set(INI_SECTION_NAME, INI_KEY_CURRENT_ROUTINE, (int)currentRoutine);
            _ini.Set(INI_SECTION_NAME, INI_KEY_ROUTINE_TO_RESUME_ON, (int)routineToResumeOn);
            _ini.Set(INI_SECTION_NAME, INI_KEY_BUFFER_VECTOR, bufferVector.ToString());
            if(currentRoutine == Routine.DetermineSunAngularSpeed) {
                _ini.Set(INI_SECTION_NAME, INI_KEY_FLOAT_LIST_AVERAGE, floatList.Average());
                _ini.Set(INI_SECTION_NAME, INI_KEY_FLOAT_LIST_COUNT, floatList.Count);
            }
            sunOrbit.WriteToIni(_ini);
            Storage = _ini.ToString();
        }
        public void Main(string argument, UpdateType updateSource) {
            Echo(Runtime.LastRunTimeMs.ToString());
            Echo(Runtime.UpdateFrequency.ToString());
            Echo(currentRoutine.ToString() + "\n");
            currentUpdateSource = updateSource;
            if((updateSource & (UpdateType.Trigger | UpdateType.Terminal)) != 0) {
                if(_commandLine.TryParse(argument)) {
                    Action currentCommand;
                    if(dicCommands.TryGetValue(_commandLine.Argument(0), out currentCommand)) currentCommand();
                    else {
                        StringBuilder printable = new StringBuilder("An invalid command was passed as an argument. Valid arguments are:\n");
                        foreach(string key in dicCommands.Keys) {
                            if(key == "Run") printable.AppendLine(key + " (default, if no command specified)");
                            else printable.AppendLine(key);
                        }
                        Echo(printable.ToString());
                    }
                }
                else dicCommands["Run"]();
            }
            else if((updateSource & UpdateType.IGC) != 0) sunOrbit.IGC_ProcessMessages();
            else dicRoutines[currentRoutine]();
        }
        public void ChangeCurrentRoutine(Routine targetRoutine) {
            UpdateFrequency updateFrequency = UpdateFrequency.Update100;
            currentRoutineRepeats = 0;
            for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].StopRotation(true);
            switch(targetRoutine) {
                case Routine.None:
                    for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].terminalBlock.GyroOverride = false;
                    updateFrequency = UpdateFrequency.None;
                    break;
                case Routine.Pause:
                    if(routineToResumeOn == Routine.DetermineSunPlaneNormalManual) targetRoutineRepeats = 3;
                    break;
                case Routine.DetermineSunPlaneNormalManual:
                    for(int i = 0; i < registeredGyros.Count; i++) registeredGyros[i].terminalBlock.GyroOverride = false;
                    break;
                case Routine.DetermineSunPlaneNormal:
                    updateFrequency = UpdateFrequency.Update10 | UpdateFrequency.Update100;
                    break;
                case Routine.AlignPanelDownToSunPlaneNormal:
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Down);
                    updateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;
                    break;
                case Routine.AlignPanelToSun:
                    UpdateExposureValues();
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Forward);
                    rotationHelper.GenerateRotatedNormalizedVectorsAroundAxisByAngle(referenceSolarPanel.WorldMatrix.Forward,
                        referenceSolarPanel.WorldMatrix.Down, Math.Acos(mostRecentSunExposure));
                    bufferVector = rotationHelper.RotatedVectorClockwise;
                    updateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;
                    break;
                case Routine.DetermineSunOrbitDirection:
                    rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Forward);
                    rotationHelper.GenerateRotatedNormalizedVectorsAroundAxisByAngle(referenceSolarPanel.WorldMatrix.Forward,
                        referenceSolarPanel.WorldMatrix.Down, 0.4f);
                    updateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;
                    break;
                case Routine.Debug:
                    foreach(Gyroscope gyro in registeredGyros) gyro.SetRotation(PrincipalAxis.Yaw, sunOrbit.AngularSpeedRadPS * sunOrbit.RotationDirection);
                    break;
            }
            currentRoutine = targetRoutine;
            Runtime.UpdateFrequency = updateFrequency;
        }
        public void UpdateExposureValues() {
            previousSunExposure = mostRecentSunExposure;
            mostRecentSunExposure = referenceSolarPanel.MaxOutput / maxPossibleOutput;
            exposureDelta = mostRecentSunExposure - previousSunExposure;
        }
        public Routine NextRoutineToMapSunOrbit() {
            Routine returnRoutine;
            if(!sunOrbit.IsMapped(SunOrbit.DataPoint.PlaneNormal)) {
                returnRoutine = Routine.DetermineSunPlaneNormalManual;
            }
            else if(!sunOrbit.IsMapped(SunOrbit.DataPoint.Direction)) {
                returnRoutine = Routine.AlignPanelDownToSunPlaneNormal;
                routineToResumeOn = Routine.DetermineSunOrbitDirection;
            }
            else if(!sunOrbit.IsMapped(SunOrbit.DataPoint.AngularSpeed)) {
                returnRoutine = Routine.AlignPanelDownToSunPlaneNormal;
                routineToResumeOn = Routine.DetermineSunAngularSpeed;
            }
            else {
                returnRoutine = Routine.None;
                routineToResumeOn = Routine.None;
            }
            return returnRoutine;
        }
        #region Sun orbit data point determination functions
        public void DetermineSunPlaneNormalManual(IMyTextSurface lcd) {
            UpdateExposureValues();
            if(lcdText.Length > lcdTextDefaultLength) lcdText.Remove(lcdTextDefaultLength + 1, lcdText.Length - lcdTextDefaultLength - 1);
            //TEST: Verify whether the rounding is accurate for non-small vanilla panels
            lcdText.Append(Math.Round(mostRecentSunExposure, 8));
            if(mostRecentSunExposure >= PRECISION_THRESHOLD) {
                if(bufferVector.IsZero()) {
                    bufferVector = referenceSolarPanel.WorldMatrix.Forward;
                    lcd.WriteText($"Exposure value {mostRecentSunExposure} has been stored,\nas it met the precision threshold of {PRECISION_THRESHOLD}.\n" +
                        $"Please wait about five seconds before resuming to align a second time.");
                    routineToResumeOn = currentRoutine;
                    ChangeCurrentRoutine(Routine.Pause);
                }
                else {
                    sunOrbit.PlaneNormal = bufferVector.Cross(referenceSolarPanel.WorldMatrix.Forward);
                    lcd.WriteText($"Success!\nA second exposure value of {mostRecentSunExposure} has been stored.\n" +
                        $"To continue, run the program again.\nNo more manual input will be required afterwards.");
                    bufferVector = Vector3D.Zero;
                    sunOrbit.IGC_ProcessMessages();
                    ChangeCurrentRoutine(Routine.None);
                }
                return;
            }
            lcd.WriteText(lcdText);
        }
        public void DetermineSunPlaneNormal() {
            //Two points are determined where sunExposure >= precision threshold
            UpdateExposureValues();
            if(mostRecentSunExposure >= PRECISION_THRESHOLD) {
                if(bufferVector.IsZero()) {
                    bufferVector = referenceSolarPanel.WorldMatrix.Forward;
                    ChangeCurrentRoutine(Routine.Pause);
                }
                else {
                    sunOrbit.PlaneNormal = bufferVector.Cross(referenceSolarPanel.WorldMatrix.Forward);
                    bufferVector = Vector3D.Zero;
                    ChangeCurrentRoutine(NextRoutineToMapSunOrbit());
                }
                return;
            }
            if(exposureDelta < 0) {
                if(exposureDelta > SUN_EXPOSURE_PAUSE_THRESHOLD) {
                    if(haveNotInvertedYetThisAxis) {
                        currentSigns[(int)currentRotationAxis] *= -1;
                        haveNotInvertedYetThisAxis = false;
                    }
                    else {
                        exposureDeltaGreaterThanZeroOnLastRun = false;
                        haveNotInvertedYetThisAxis = true;
                        currentRotationAxis = 1 - currentRotationAxis;
                        return;
                    }
                }
                else {
                    ChangeCurrentRoutine(Routine.Pause);
                    return;
                }
            }
            else if((currentUpdateSource & UpdateType.Update100) != 0 && exposureDelta > 0) {
                if(exposureDeltaGreaterThanZeroOnLastRun) haveNotInvertedYetThisAxis = false;
                else exposureDeltaGreaterThanZeroOnLastRun = true;
            }
            //TODO: Make this value non-constant and able to adapt to current sunExposure progress
            float currentAngularMomentum = Math.Max((1 - mostRecentSunExposure) / 2, 0.01f) * currentSigns[(int)currentRotationAxis];
            for(int i = 0; i < registeredGyros.Count; i++) { registeredGyros[i].SetRotation(currentRotationAxis, currentAngularMomentum); }
        }
        public void DetermineSunOrbitDirection() {
            if(Gyroscope.AlignToTargetNormalizedVector(rotationHelper.RotatedVectorClockwise, referenceSolarPanel.WorldMatrix, rotationHelper, registeredGyros)
                && (currentUpdateSource & UpdateType.Update100) != 0) {
                UpdateExposureValues();
                if(currentRoutineRepeats > 0) floatList.Add(Math.Sign(exposureDelta));
                currentRoutineRepeats++;
                if(floatList.Count >= MEASUREMENTS_TARGET_AMOUNT_SUN_DIRECTION) {
                    sunOrbit.RotationDirection = Math.Sign(floatList.Average());
                    floatList.Clear();
                    ChangeCurrentRoutine(NextRoutineToMapSunOrbit());
                }
            }
        }
        public void DetermineSunAngularSpeed() {
            UpdateExposureValues();
            if(!rotationHelper.IsAlignedWithNormalizedTargetVector(sunOrbit.PlaneNormal, referenceSolarPanel.WorldMatrix.Down) || mostRecentSunExposure < 0.8f) {
                routineToResumeOn = currentRoutine;
                ChangeCurrentRoutine(Routine.AlignPanelDownToSunPlaneNormal);
            }
            else {
                if(currentRoutineRepeats > 0) floatList.Add((float)Math.Abs((Math.Acos(mostRecentSunExposure) - Math.Acos(previousSunExposure)) / (5d / 3)));
                currentRoutineRepeats++;
                //TODO: Increase the amount of data points needed or find a better algorithm that doesn't require as many data points, eventually
                if(floatList.Count >= MEASUREMENTS_TARGET_AMOUNT_SUN_SPEED) {
                    sunOrbit.AngularSpeedRadPS = floatList.Average() * sunOrbit.RotationDirection;
                    floatList.Clear();
                    ChangeCurrentRoutine(NextRoutineToMapSunOrbit());
                }
                lcd.WriteText($"mostRecentExposure: {mostRecentSunExposure}\npreviousExposure: {previousSunExposure}\nexposureDelta: {exposureDelta}\n" +
                    $"floatListCount: {floatList.Count}\n" +
                    $"angleAcosAverage: {(floatList.Count > 0 ? floatList.Average() : 0)}\n");
            }
        }
        #endregion
        #region Initialization functions
        public void InitializeBlocks() {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType(blocks, block => block.IsSameConstructAs(Me) && block.IsFunctional);
            referenceSolarPanel = GetBlock<IMySolarPanel>(NAME_REFERENCE_SOLAR_PANEL, blocks);
            maxPossibleOutput = referenceSolarPanel.CubeGrid.GridSizeEnum == MyCubeSize.Small ? 0.04f : 0.16f; //in MW
            lcd = GetBlock<IMyTextPanel>(NAME_LCD, blocks);

            registeredGyros.Clear();
            List<IMyGyro> gyroList = new List<IMyGyro>();
            GridTerminalSystem.GetBlocksOfType(gyroList, gyro => gyro.IsSameConstructAs(Me));
            foreach(IMyGyro gyro in gyroList) registeredGyros.Add(new Gyroscope(referenceSolarPanel, gyro));
        }
        public T GetBlock<T>(string blockName = "", List<IMyTerminalBlock> blocks = null) where T : IMyTerminalBlock {
            var blocksLocal = blocks ?? new List<IMyTerminalBlock>(); ;
            T myBlock = (T)GridTerminalSystem.GetBlockWithName(blockName);
            if(!(myBlock is object) && !(blocks is object)) GridTerminalSystem.GetBlocks(blocksLocal);
            if(!(myBlock is object)) myBlock = (T)blocksLocal.Find(block => block.GetType().Name == typeof(T).Name.Substring(1));
            if(myBlock is object) return myBlock;
            else throw new Exception($"An owned block of type {typeof(T).Name} does not exist in the provided block list.");
        }
        #endregion
    }
}
