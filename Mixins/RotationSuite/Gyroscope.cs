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
    public class Gyroscope {
        //Stores a gyroblock and the axes it would have to rotate under to align to a given target, assuming the block whose vectors to align are not the gyro itsself
        //e.g. align a solar panel, gyro is rotated 90 degrees on all axes relative to said panel --> actual rotation needs to occur on corresponding axes
        readonly public IMyGyro terminalBlock;
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
            this.terminalBlock = gyroBlock;
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
        public static bool AlignToTargetNormalizedVector(Vector3D target, MatrixD anchorToAlignWorldMatrix, RotationHelper rotationHelper, List<Gyroscope> gyroList,
            float alignmentSuccessThreshold = 0.0001f, float speedLimitInRadPS = 0.75f) {
            //TODO: Make this work when alignment into the opposite direction is true (as the dot product is 0 there as well, and that's the opposite of our requested alignment)
            //TODO: Allow this to be maximally quick by giving the option to input ship mass (with gyro rotation force and ship mass we know exactly when to decelerate, reaching maximum speeds at any time)
            //i.e. maybe by using a previously determined and stored in this class weight coefficient that multiplies these numbers here
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
            float gyroPower = powerToFull || terminalBlock.GyroPower == 0 ? 1 : terminalBlock.GyroPower;
            terminalBlock.Enabled = true;
            terminalBlock.GyroOverride = true;
            terminalBlock.GyroPower = gyroPower;
        }
        protected void SetRotation(MyTuple<PrincipalAxis, int> rotationAxis, float radPerSecond) {
            Enable(true);
            switch(rotationAxis.Item1) {
                case PrincipalAxis.Pitch:
                    terminalBlock.Pitch = rotationAxis.Item2 * radPerSecond;
                    break;
                case PrincipalAxis.Yaw:
                    terminalBlock.Yaw = rotationAxis.Item2 * radPerSecond;
                    break;
                case PrincipalAxis.Roll:
                    terminalBlock.Roll = rotationAxis.Item2 * radPerSecond;
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
            terminalBlock.Pitch = 0;
            terminalBlock.Yaw = 0;
            terminalBlock.Roll = 0;
            terminalBlock.GyroOverride = leaveOverrideOn;
        }
    }
}
