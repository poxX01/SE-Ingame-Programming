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
        float currentOutput;
        float previousOutput;
        IMySolarPanel panel;
        IMyRemoteControl rc;
        IMyTextPanel lcd;

        Vector3D sunPlaneNormal = new Vector3D(-0.342329600519368, 0.704241212764077, 0.621976493810523);
        Vector3D targetVec = new Vector3D();
        bool alignToSun = false;
        bool measureSpeed = false;
        bool debugPrototype = false;
        bool areOntheRightPath = false;
        bool currentlyReversing = false;

        float outputPreAlignment;
        float preliminaryAngularSpeed;
        float exposureDelta;
        float previousExposureDelta;
        float leastDeltaYet;


        List<float> radiansMovedPer100Ticks = new List<float>();
        int targetDigit;
        int currentRoutineRepeats;

        MyCommandLine _cmdLine = new MyCommandLine();
        List<Gyroscope> registeredGyros = new List<Gyroscope>();
        RotationHelper rotationHelper = new RotationHelper();
        public enum PrincipalAxis { Pitch, Yaw, Roll }
        public class RotationHelper {
            //The following two are mostly only useful for gyroscope rotation, as all 3 axes are available to us.
            //E.g. I want my RC.Forward to align to (0, 1, 0), I require the axes that aren't irrelevant in that rotation (Roll is), and those respective axes' planeNormals for the dot product measure
            private PrincipalAxis[] _rotationAxes = new PrincipalAxis[2]; //The gyroscope operates via Pitch/Yaw/Roll, so the respective axes are stored for a given rotation task.
            private Base6Directions.Direction[] _dotProductFactorDirections = new Base6Directions.Direction[2]; //As alignment is done by setting the dot product of 2 axes to 0, the directions for those two axes needs to be stored
            public PrincipalAxis[] RotationAxes { get { return _rotationAxes; } }
            public Base6Directions.Direction[] DotProductFactorDirections { get { return _dotProductFactorDirections; } }
            public Vector3D RotatedVectorClockwise { get; private set; }
            public Vector3D RotatedVectorCounterClockwise { get; private set; }
            public void DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction directionToAlignToAnyFutureTarget) {
                switch(directionToAlignToAnyFutureTarget) {
                    case Base6Directions.Direction.Forward:
                        _rotationAxes[0] = PrincipalAxis.Pitch;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Up;
                        _rotationAxes[1] = PrincipalAxis.Yaw;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Right;
                        break;
                    case Base6Directions.Direction.Backward:
                        _rotationAxes[0] = PrincipalAxis.Pitch;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Down;
                        _rotationAxes[1] = PrincipalAxis.Yaw;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Left;
                        break;
                    case Base6Directions.Direction.Right:
                        _rotationAxes[0] = PrincipalAxis.Yaw;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Backward;
                        _rotationAxes[1] = PrincipalAxis.Roll;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Down;
                        break;
                    case Base6Directions.Direction.Left:
                        _rotationAxes[0] = PrincipalAxis.Yaw;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Forward;
                        _rotationAxes[1] = PrincipalAxis.Roll;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Up;
                        break;
                    case Base6Directions.Direction.Up:
                        _rotationAxes[0] = PrincipalAxis.Pitch;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Backward;
                        _rotationAxes[1] = PrincipalAxis.Roll;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Right;
                        break;
                    case Base6Directions.Direction.Down:
                        _rotationAxes[0] = PrincipalAxis.Pitch;
                        _dotProductFactorDirections[0] = Base6Directions.Direction.Forward;
                        _rotationAxes[1] = PrincipalAxis.Roll;
                        _dotProductFactorDirections[1] = Base6Directions.Direction.Left;
                        break;
                }
            }
            public void GenerateRotatedNormalizedVectorsAroundAxisByAngle(Vector3D vectorToRotate, Vector3D rotationAxis, double angle) {
                vectorToRotate.Normalize();
                rotationAxis.Normalize();
                RotatedVectorClockwise = Vector3D.Transform(vectorToRotate, MatrixD.CreateFromQuaternion(QuaternionD.CreateFromAxisAngle(rotationAxis, angle)));
                RotatedVectorCounterClockwise = Vector3D.Transform(vectorToRotate, MatrixD.CreateFromQuaternion(QuaternionD.CreateFromAxisAngle(rotationAxis, -angle)));
            }
        }
        public class Gyroscope {
            //Stores a gyroblock and the axes it would have to rotate under to align to a given target, assuming the block whose vectors to align are not the gyro itsself
            //e.g. align a solar panel, gyro is rotated 90 degrees on all axes relative to said panel --> actual rotation needs to occur on corresponding axes
            public IMyGyro gyroBlock;
            protected MyTuple<PrincipalAxis, int> pitchCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            protected MyTuple<PrincipalAxis, int> yawCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            protected MyTuple<PrincipalAxis, int> rollCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            public Gyroscope(IMyTerminalBlock originBlock, IMyGyro gyroBlock) {
                //Grid pivot axes: blue = Forward, red = Right, green = Up
                //Respective rotation axes for PYR via Right Hand Rule (direction of non-thumb fingers pointing counter-clockwise, positive rotation direction) are:
                //Pitch: Right (+rotates upward, -rotates downward)
                //Yaw: Down (+rotes right, -rotates left)
                //Roll: Forward (+tilts rightside, -tilts leftside)
                //Gyros are odd. Assigning a negative value to pitch makes it a positive value in the terminal and vice versa, meaning that in programming the change of pitch is inverted
                //Therefore, our pitch axis in programming for gyros has to use the left hand rule. Truly horrendous.
                //To stick to the right hand rule persistently, we'll invert the signs for Right and Left in DetermineMatchingRotationAxis().
                Vector3D originPitchAxis = originBlock.WorldMatrix.Right;
                Vector3D originYawAxis = originBlock.WorldMatrix.Down;
                Vector3D originRollAxis = originBlock.WorldMatrix.Forward;
                Vector3D currentComparisonVector;
                foreach(Base6Directions.Direction direction in Base6Directions.EnumDirections) {
                    currentComparisonVector = gyroBlock.WorldMatrix.GetDirectionVector(direction);
                    if(currentComparisonVector == originPitchAxis) pitchCorrespondingAxis = DetermineMatchingRotationAxis(direction);
                    else if(currentComparisonVector == originYawAxis) yawCorrespondingAxis = DetermineMatchingRotationAxis(direction);
                    else if(currentComparisonVector == originRollAxis) rollCorrespondingAxis = DetermineMatchingRotationAxis(direction);
                }
                this.gyroBlock = gyroBlock;
            }
            private static MyTuple<PrincipalAxis, int> DetermineMatchingRotationAxis(Base6Directions.Direction direction) {
                int sign = 1;
                switch(direction) {
                    case Base6Directions.Direction.Right: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Pitch, -sign);
                    case Base6Directions.Direction.Down: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Yaw, sign);
                    case Base6Directions.Direction.Forward: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Roll, sign);
                    case Base6Directions.Direction.Left: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Pitch, sign);
                    case Base6Directions.Direction.Up: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Yaw, -sign);
                    case Base6Directions.Direction.Backward: return new MyTuple<PrincipalAxis, int>(PrincipalAxis.Roll, -sign);
                    default: throw new Exception("Gyroscope.DetermineMatchingRotationAxis() was called with no valid Base6Directions.Direction.");
                }
            }
            public static void DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction directionToAlignToFutureGivenTarget,
                out PrincipalAxis[] rotationAxes, out Base6Directions.Direction[] dotProductFactorDirections) {
                rotationAxes = new PrincipalAxis[2];
                dotProductFactorDirections = new Base6Directions.Direction[2];
                switch(directionToAlignToFutureGivenTarget) {
                    case Base6Directions.Direction.Forward:
                        rotationAxes[0] = PrincipalAxis.Pitch;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Up;
                        rotationAxes[1] = PrincipalAxis.Yaw;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Right;
                        break;
                    case Base6Directions.Direction.Backward:
                        rotationAxes[0] = PrincipalAxis.Pitch;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Down;
                        rotationAxes[1] = PrincipalAxis.Yaw;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Left;
                        break;
                    case Base6Directions.Direction.Right:
                        rotationAxes[0] = PrincipalAxis.Yaw;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Backward;
                        rotationAxes[1] = PrincipalAxis.Roll;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Down;
                        break;
                    case Base6Directions.Direction.Left:
                        rotationAxes[0] = PrincipalAxis.Yaw;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Forward;
                        rotationAxes[1] = PrincipalAxis.Roll;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Up;
                        break;
                    case Base6Directions.Direction.Up:
                        rotationAxes[0] = PrincipalAxis.Pitch;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Backward;
                        rotationAxes[1] = PrincipalAxis.Roll;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Right;
                        break;
                    case Base6Directions.Direction.Down:
                        rotationAxes[0] = PrincipalAxis.Pitch;
                        dotProductFactorDirections[0] = Base6Directions.Direction.Forward;
                        rotationAxes[1] = PrincipalAxis.Roll;
                        dotProductFactorDirections[1] = Base6Directions.Direction.Left;
                        break;
                }
            }
            public static bool AlignToTargetNormalizedVector(Vector3D target, MatrixD anchorToAlignWorldMatrix, RotationHelper rotationHelper, List<Gyroscope> gyroList,
                float alignmentSuccessThreshold = 0.0001f, float speedLimitInRadPS = 0.75f) {
                //TODO: Make this work when alignment into the opposite direction is true (as the dot product is 0 there as well, and that's the opposite of our requested alignment)
                //TODO: Allow this to be maximally quick by giving the option to input ship mass (with gyro rotation force and ship mass we know exactly when to decelerate, reaching maximum speeds at any time)
                //As this method is designed to be run on UpdateFrequency.Update1, it is optimized for performance. Maybe the float instantiation could be moved however.
                //Aligning to a target with a certain vector requires the two rotation axes used to align need to be in perpendicular position to the target, i.e. a 0 dot product
                float rotationRemainder0 = (float)anchorToAlignWorldMatrix.GetDirectionVector(rotationHelper.DotProductFactorDirections[0]).Dot(target) * 10;
                float rotationRemainder1 = (float)anchorToAlignWorldMatrix.GetDirectionVector(rotationHelper.DotProductFactorDirections[1]).Dot(target) * 10;
                rotationRemainder0 = rotationRemainder0 > speedLimitInRadPS ? speedLimitInRadPS : rotationRemainder0 < -speedLimitInRadPS ? -speedLimitInRadPS : rotationRemainder0;
                rotationRemainder1 = rotationRemainder1 > speedLimitInRadPS ? speedLimitInRadPS : rotationRemainder1 < -speedLimitInRadPS ? -speedLimitInRadPS : rotationRemainder1;
                for(int i = 0; i < gyroList.Count; i++) {
                    gyroList[i].SetRotation(rotationHelper.RotationAxes[0], rotationRemainder0);
                    gyroList[i].SetRotation(rotationHelper.RotationAxes[1], rotationRemainder1);
                }
                return Math.Abs(rotationRemainder0) < alignmentSuccessThreshold && Math.Abs(rotationRemainder1) < alignmentSuccessThreshold;
            }
            protected void Enable(bool powerToFull = false) {
                float gyroPower = powerToFull || gyroBlock.GyroPower == 0 ? 1 : gyroBlock.GyroPower;
                gyroBlock.Enabled = true;
                gyroBlock.GyroOverride = true;
                gyroBlock.GyroPower = gyroPower;
            }
            protected void SetRotation(MyTuple<PrincipalAxis, int> rotationAxis, float radPerSecond) {
                Enable(true);
                switch(rotationAxis.Item1) {
                    case PrincipalAxis.Pitch:
                        gyroBlock.Pitch = rotationAxis.Item2 * radPerSecond;
                        break;
                    case PrincipalAxis.Yaw:
                        gyroBlock.Yaw = rotationAxis.Item2 * radPerSecond;
                        break;
                    case PrincipalAxis.Roll:
                        gyroBlock.Roll = rotationAxis.Item2 * radPerSecond;
                        break;
                }
            }
            public void SetRotation(PrincipalAxis axisToRotateAround, float radPerSecond) {
                switch(axisToRotateAround) {
                    case PrincipalAxis.Pitch:
                        SetRotation(pitchCorrespondingAxis, radPerSecond);
                        break;
                    case PrincipalAxis.Yaw:
                        SetRotation(yawCorrespondingAxis, radPerSecond);
                        break;
                    case PrincipalAxis.Roll:
                        SetRotation(rollCorrespondingAxis, radPerSecond);
                        break;
                }
            }
            public void StopRotation(bool leaveOverrideOn = false) {
                Enable();
                gyroBlock.Pitch = 0;
                gyroBlock.Yaw = 0;
                gyroBlock.Roll = 0;
                gyroBlock.GyroOverride = leaveOverrideOn;
            }
        }
        public Program() {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(blocks);
            panel = GetBlock<IMySolarPanel>("", blocks);
            rc = GetBlock<IMyRemoteControl>("", blocks);
            lcd = GetBlock<IMyTextPanel>("", blocks);
            lcd.ContentType = ContentType.TEXT_AND_IMAGE;
            lcd.FontColor = Color.Crimson;

            var gyroList = new List<IMyGyro>();
            GridTerminalSystem.GetBlocksOfType(gyroList);
            gyroList.ForEach(gyro => registeredGyros.Add(new Gyroscope(rc, gyro)));

            Me.CustomData = "";
        }
        public void Main(string argument, UpdateType updateSource) {
            previousOutput = currentOutput;
            currentOutput = panel.MaxOutput;
            previousExposureDelta = exposureDelta;
            exposureDelta = currentOutput - previousOutput;
            if((updateSource & UpdateType.Update1) != 0) {
                if(alignToSun) {
                    if(Gyroscope.AlignToTargetNormalizedVector(targetVec, rc.WorldMatrix, rotationHelper, registeredGyros, 0.0001f)) {
                        if(currentOutput < outputPreAlignment) targetVec = rotationHelper.RotatedVectorCounterClockwise;
                        else {
                            Runtime.UpdateFrequency = UpdateFrequency.None;
                            foreach(var gyro in registeredGyros) gyro.StopRotation(true);
                            alignToSun = false;
                        }
                    }
                }
                else {
                    if(Gyroscope.AlignToTargetNormalizedVector(sunPlaneNormal, rc.WorldMatrix, rotationHelper, registeredGyros, 0.0001f)) {
                        foreach(var gyro in registeredGyros) gyro.StopRotation(true);
                        Runtime.UpdateFrequency = UpdateFrequency.None;
                    }
                }
            }
            else if((updateSource & UpdateType.Update100) != 0) {
                lcd.WriteText($"currentOutput: {currentOutput}\npreviousOutput: {previousOutput}\ndelta: {currentOutput - previousOutput}\n" +
                    $"gyro[0].Yaw: {registeredGyros[0].gyroBlock.Yaw}\n");
                if(debugPrototype) {
                    IncrementOneAtDigit(preliminaryAngularSpeed, targetDigit, currentlyReversing);
                    if(previousExposureDelta < exposureDelta) {
                        areOntheRightPath = true;
                        leastDeltaYet = exposureDelta;
                    }
                    else if(previousExposureDelta > exposureDelta) currentlyReversing = !currentlyReversing;
                }
                else if(measureSpeed) {
                    if(currentOutput == previousOutput) return;
                    radiansMovedPer100Ticks.Add((float)Math.Abs(Math.Acos(currentOutput * 25) - Math.Acos(previousOutput * 25)));
                    currentRoutineRepeats++;
                    if(currentRoutineRepeats > 20) {
                        radiansMovedPer100Ticks.RemoveAt(0);
                        for(int i = 0; i < radiansMovedPer100Ticks.Count; i++) Me.CustomData += radiansMovedPer100Ticks[i] + "\n";
                        Me.CustomData += $"\naverage: {radiansMovedPer100Ticks.Average()}";
                        Runtime.UpdateFrequency = UpdateFrequency.None;
                        measureSpeed = false;
                        Echo("Done gathering preliminary speed.");
                    }
                }
            }
            else if(argument == "prototype") {
                char[] valueChars = preliminaryAngularSpeed.ToString().Substring(2).ToCharArray(); //First two characters are 0 and ., so they are irrelevant.
                for(int i = 0; i < valueChars.Length; i++) if(valueChars[i] != '0') targetDigit = i;
                debugPrototype = true;
                currentRoutineRepeats = 0;
                Runtime.UpdateFrequency = UpdateFrequency.Update100;
            }
            else if(argument == "alignToSun") {
                Runtime.UpdateFrequency = UpdateFrequency.Update1;
                alignToSun = true;
                rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Forward);
                rotationHelper.GenerateRotatedNormalizedVectorsAroundAxisByAngle(rc.WorldMatrix.Forward, rc.WorldMatrix.Down, Math.Acos(currentOutput * 25));
                outputPreAlignment = currentOutput;
                targetVec = rotationHelper.RotatedVectorClockwise;
            }
            else if(argument == "alignToPlaneNormal") {
                Runtime.UpdateFrequency = UpdateFrequency.Update1;
                rotationHelper.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Down);
            }
            else if(argument == "startSpeedMeasure") {
                radiansMovedPer100Ticks.Clear();
                Runtime.UpdateFrequency = UpdateFrequency.Update100;
                measureSpeed = true;
            }
            else if(argument == "printOutput") Runtime.UpdateFrequency = UpdateFrequency.Update100;
            else if(_cmdLine.TryParse(argument)) {
                if(_cmdLine.Argument(0) == "rotate") {
                    float speed = (float)(1f / (float.Parse(_cmdLine.Argument(1)) * 60) * 2 * Math.PI);
                    foreach(var gyro in registeredGyros) gyro.SetRotation(PrincipalAxis.Yaw, speed);
                }
            }
        }
        public float IncrementOneAtDigit(float input, int digitToModify, bool reverse = false) {
            int tenPower = (int)Math.Pow(10, digitToModify);
            int digitModifier = reverse ? -1 : 1;
            int valueAtDigit = (int)Math.Round(input * tenPower % 10);
            if(valueAtDigit == 9 && reverse) digitModifier = -9;
            else if(valueAtDigit == 0 && !reverse) digitModifier = 9;
            return (input * tenPower + digitModifier) / tenPower;
        }
        public T GetBlock<T>(string blockName = "", List<IMyTerminalBlock> blocks = null) {
            var blocksLocal = blocks ?? new List<IMyTerminalBlock>(); ;
            T myBlock = (T)GridTerminalSystem.GetBlockWithName(blockName);
            if(!(myBlock is object) && !(blocks is object)) GridTerminalSystem.GetBlocks(blocksLocal);
            if(!(myBlock is object)) myBlock = (T)blocksLocal.Find(block => block.GetType().Name == typeof(T).Name.Substring(1));
            if(myBlock is object) return myBlock;
            else throw new Exception($"An owned block of type {typeof(T).Name} does not exist in the provided block list.");
        }
        public void Main(string argument) {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(blocks);
            IMyGyro gyro = GetBlock<IMyGyro>("", blocks);

            int x = 0;
            int.TryParse(argument, out x);
            float f = 12.3456789f;
            gyro.Yaw = (float)(f + 1f / Math.Pow(10, x));
            Echo(gyro.Yaw.ToString());
            Echo(f.ToString());
            //Echo($"{0.001f + (1f / Math.Pow(10, x))}\nf: {f}");
            //Echo($"{(float)(2 * Math.PI / (0.00144866f / (100f / 60)) / 60)}");
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