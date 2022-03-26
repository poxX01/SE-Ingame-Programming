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
        //WARNING: This script is preliminary trash. Do not expect optimization or elegant coding solutions!
        //TODO: Scale return of RotationRemainder() properly (maybe exponentially, base10) in order not to overshoot at large and to be too slow at low remainders.
        //This aligner relies on the gyro(s) being exactly aligned with the RC
        const bool ALIGN_TO_POSITION = true;
        Vector3D targetPosition = new Vector3D(0, 0, 0); //will align to this target
        Vector3D targetDirection = new Vector3D(-0.859708428382874, 0.0388918220996857, -0.509302258491516);//or towards this direction
        IMyTextPanel lcd;
        IMyRemoteControl rc;
        List<Gyroscope> gyroList = new List<Gyroscope>();
        bool doAlign = false;
        Vector3D vecTargetAlignment;
        Gyroscope.PrincipalAxis[] rotationAxes;
        Base6Directions.Direction[] dotProductFactorDirections;
        public class Gyroscope {
            public IMyGyro gyroBlock;
            protected MyTuple<PrincipalAxis, int> pitchCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            protected MyTuple<PrincipalAxis, int> yawCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            protected MyTuple<PrincipalAxis, int> rollCorrespondingAxis = new MyTuple<PrincipalAxis, int>();
            public enum PrincipalAxis { Pitch, Yaw, Roll }
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
            public static void DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction directionToAlignToTarget,
                out PrincipalAxis[] rotationAxes, out Base6Directions.Direction[] dotProductFactorDirections) {
                rotationAxes = new PrincipalAxis[2];
                dotProductFactorDirections = new Base6Directions.Direction[2];
                switch(directionToAlignToTarget) {
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
            public static bool AlignToTarget(Vector3D target, MatrixD anchorToAlignWorldMatrix, PrincipalAxis[] rotationAxes,
                Base6Directions.Direction[] dotProductFactorDirections, List<Gyroscope> gyroList,
                float alignmentSuccessThreshold = 0.0001f, float speedLimitInRadPS = 0.75f) {
                //TODO: Make this work when alignment into the opposite direction is true
                //As this method is designed to be run on UpdateFrequency.Update1, it is optimized for performance.
                //Aligning to a target with a certain vector requires the two rotation axes used to align need to be in perpendicular position to the target, i.e. a 0 dot product
                float rotationRemainder0 = (float)anchorToAlignWorldMatrix.GetDirectionVector(dotProductFactorDirections[0]).Dot(target) * 10;
                float rotationRemainder1 = (float)anchorToAlignWorldMatrix.GetDirectionVector(dotProductFactorDirections[1]).Dot(target) * 10;
                rotationRemainder0 = rotationRemainder0 > speedLimitInRadPS ? speedLimitInRadPS : rotationRemainder0 < -speedLimitInRadPS ? -speedLimitInRadPS : rotationRemainder0;
                rotationRemainder1 = rotationRemainder1 > speedLimitInRadPS ? speedLimitInRadPS : rotationRemainder1 < -speedLimitInRadPS ? -speedLimitInRadPS : rotationRemainder1;
                for(int i = 0; i < gyroList.Count; i++) {
                    gyroList[i].SetRotation(rotationAxes[0], rotationRemainder0);
                    gyroList[i].SetRotation(rotationAxes[1], rotationRemainder1);
                }
                return Math.Abs(rotationRemainder0) < alignmentSuccessThreshold && Math.Abs(rotationRemainder1) < alignmentSuccessThreshold;
            }
            protected void Enable() {
                gyroBlock.Enabled = true;
                gyroBlock.GyroOverride = true;
                gyroBlock.GyroPower = 1;
            }
            protected void SetRotation(MyTuple<PrincipalAxis, int> rotationAxis, float radPerSecond) {
                Enable();
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
            public void StopRotation() {
                Enable();
                gyroBlock.Pitch = 0;
                gyroBlock.Yaw = 0;
                gyroBlock.Roll = 0;
                gyroBlock.GyroOverride = false;
            }
        }
        public Program() {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(blocks);
            rc = GetBlock<IMyRemoteControl>("", blocks);
            lcd = GetBlock<IMyTextPanel>("", blocks);
            var gyroBlocks = new List<IMyGyro>();
            GridTerminalSystem.GetBlocksOfType(gyroBlocks);
            gyroBlocks.ForEach(gyro => gyroList.Add(new Gyroscope(rc, gyro)));

            lcd.ContentType = ContentType.TEXT_AND_IMAGE;
            lcd.FontColor = Color.LightGoldenrodYellow;

            Gyroscope.DetermineAlignmentRotationAxesAndDirections(Base6Directions.Direction.Forward, out rotationAxes, out dotProductFactorDirections);
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
        }
        public void Main(string argument, UpdateType updateSource) {
            vecTargetAlignment = Vector3D.Normalize(ALIGN_TO_POSITION ? targetPosition - rc.WorldMatrix.Translation : targetDirection);

            if((updateSource & (UpdateType.Trigger | UpdateType.Terminal)) != 0) { 
                doAlign = !doAlign;
                Runtime.UpdateFrequency = doAlign ? UpdateFrequency.Update1 : UpdateFrequency.None;
            }
            if(doAlign) lcd.WriteText($"Has aligned: {Gyroscope.AlignToTarget(vecTargetAlignment, rc.WorldMatrix, rotationAxes, dotProductFactorDirections, gyroList)}");
            else gyroList.ForEach(gyro => gyro.StopRotation());
        }
        public T GetBlock<T>(string blockName = "", List<IMyTerminalBlock> blocks = null) {
            var blocksLocal = blocks ?? new List<IMyTerminalBlock>(); ;
            T myBlock = (T)GridTerminalSystem.GetBlockWithName(blockName);
            if(!(myBlock is object) && !(blocks is object)) GridTerminalSystem.GetBlocks(blocksLocal);
            if(!(myBlock is object)) myBlock = (T)blocksLocal.Find(block => block.GetType().Name == typeof(T).Name.Substring(1));
            if(myBlock is object) return myBlock;
            else throw new Exception($"An owned block of type {typeof(T).Name} does not exist in the provided block list.");
        }


        public Vector3D NormalizedTargetVector(Vector3D target, MatrixD anchorWorldMatrix) {
            //target can be either a position or a normalized target vector
            //if target is non-normalized and is the same position as the anchorBlock, returns (0,0,1)
            Vector3D targetVec = target.Length() == 1 ? target : Vector3D.Normalize(anchorWorldMatrix.Translation - target);
            if(targetVec.IsZero()) return new Vector3D(0, 0, 1);
            else return targetVec;
        }
    }
}
