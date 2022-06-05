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
        public enum PrincipalAxis { Pitch, Yaw, Roll }
        public sealed class RotationHelper {
            //The following two are mostly only useful for gyroscope rotation, as all 3 axes are available to us.
            //E.g. I want my RC.Forward to align to (0, 1, 0), I require the axes that aren't irrelevant in that rotation (Roll is), and those respective axes' planeNormals for the dot product measure
            readonly private PrincipalAxis[] _rotationAxes = new PrincipalAxis[2]; //The gyroscope operates via Pitch/Yaw/Roll, so the respective axes are stored for a given rotation task.
            readonly private Base6Directions.Direction[] _dotProductFactorDirections = new Base6Directions.Direction[2]; //As alignment is done by setting the dot product of 2 axes to 0, the directions for those two axes needs to be stored
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
            public bool IsAlignedWithNormalizedTargetVector(Vector3D targetVec, Vector3D measureVec, float alignmentSuccessThreshold = 0.0001f) {
                return Vector3D.Dot(targetVec, measureVec) >= 1 - alignmentSuccessThreshold;
            }
            public Vector3D NormalizedVectorProjectedOntoPlane(Vector3D vecToProject, Vector3D planeNormal) {
                vecToProject.Normalize();
                planeNormal.Normalize();
                return Vector3D.Normalize(vecToProject - vecToProject.Dot(planeNormal) * planeNormal);
            }
        }
        public class Rotor {
            private const float ANGLE_LIMIT = (float)Math.PI * 2;
            public const float ALIGNMENT_PRECISION_THRESHOLD = 0.0000001f;
            readonly public IMyMotorStator terminalBlock;
            protected virtual float AngleLimit { get { return ANGLE_LIMIT; } }
            public Vector3D LocalRotationAxis { get { return terminalBlock.WorldMatrix.Up; } }
            public Rotor(IMyMotorStator terminalBlock) {
                this.terminalBlock = terminalBlock;
            }
            protected void Enable() {
                terminalBlock.Enabled = true;
                terminalBlock.Torque = 1000;
            }
            public void Unlock() {
                Enable();
                terminalBlock.RotorLock = false;
                terminalBlock.TargetVelocityRad = 0;
                terminalBlock.UpperLimitRad = float.MaxValue;
                terminalBlock.LowerLimitRad = float.MinValue;
            }
            public void Lock() {
                Enable();
                terminalBlock.RotorLock = true;
                terminalBlock.TargetVelocityRad = 0;
            }
            public double AlignToVector(RotationHelper rhInstance, Vector3D origin, Vector3D target) {
                return AlignToVector(rhInstance, origin, target, LocalRotationAxis);
            }
            /// <summary>Intended to be called only once on a non-moving rotor. Alignment measurements are imprecise when rotors are moving.</summary>
            /// <param name="alternateRotationAxis">Used instead of LocalRotationAxis to calculate the angle of rotation.</param>
            public double AlignToVector(RotationHelper rhInstance, Vector3D origin, Vector3D target, Vector3D alternateRotationAxis) {
                if(rhInstance.IsAlignedWithNormalizedTargetVector(target, origin, ALIGNMENT_PRECISION_THRESHOLD)) return 0;
                origin = rhInstance.NormalizedVectorProjectedOntoPlane(origin, alternateRotationAxis);
                target = rhInstance.NormalizedVectorProjectedOntoPlane(target, alternateRotationAxis);
                double theta = Math.Acos(origin.Dot(target));
                theta *= Math.Sign(origin.Cross(alternateRotationAxis).Dot(target));
                RotateByAngle(theta);
                return theta;
            }
            public virtual void RotateByAngle(double angleDelta) {
                float targetAngle = terminalBlock.Angle + (float)angleDelta;
                Unlock();
                if(targetAngle > AngleLimit) {
                    terminalBlock.LowerLimitRad = SignSwappedAngle(terminalBlock.Angle);
                    terminalBlock.UpperLimitRad = ClampedAngleWithinLimit(targetAngle);
                    terminalBlock.TargetVelocityRPM = 1;
                }
                else if(targetAngle < -AngleLimit) {
                    terminalBlock.UpperLimitRad = SignSwappedAngle(terminalBlock.Angle);
                    terminalBlock.LowerLimitRad = ClampedAngleWithinLimit(targetAngle);
                    terminalBlock.TargetVelocityRPM = -1;
                }
                else if(targetAngle < terminalBlock.Angle) {
                    terminalBlock.LowerLimitRad = targetAngle;
                    terminalBlock.UpperLimitRad = terminalBlock.Angle;
                    terminalBlock.TargetVelocityRPM = -1;
                }
                else {
                    terminalBlock.UpperLimitRad = targetAngle;
                    terminalBlock.LowerLimitRad = terminalBlock.Angle;
                    terminalBlock.TargetVelocityRPM = 1;
                }
            }
            private float ClampedAngleWithinLimit(float angle) {
                //Only works if 2*-AngleLimit >= angle <= 2*AngleLimit
                float returnAngle;
                if(angle < AngleLimit && angle > -AngleLimit) returnAngle = angle;
                else if(angle < 0) returnAngle = angle + AngleLimit;
                else returnAngle = angle - AngleLimit;
                return returnAngle;
            }
            protected float SignSwappedAngle(float angle) {
                float returnAngle;
                if(angle < 0) returnAngle = AngleLimit + angle;
                else returnAngle = -AngleLimit + angle;
                return returnAngle;
            }
        }
        public sealed class Hinge : Rotor {
            private const float ANGLE_LIMIT = (float)Math.PI / 2;
            protected override float AngleLimit { get { return ANGLE_LIMIT; } }
            /// <summary>
            /// Gets the top part's facing vector, if one is attached. Otherwise returns Vector3D.Zero.
            /// </summary>
            public Vector3D HingeFacing {
                get {
                    if(terminalBlock.IsAttached) return terminalBlock.Top.WorldMatrix.Left;
                    else return Vector3D.Zero;
                }
            }
            public Hinge(IMyMotorStator terminalBlock) : base(terminalBlock) { }
            public new void Unlock() {
                Enable();
                terminalBlock.RotorLock = false;
                terminalBlock.TargetVelocityRad = 0;
                terminalBlock.UpperLimitRad = AngleLimit;
                terminalBlock.LowerLimitRad = -AngleLimit;
            }
            public override void RotateByAngle(double angleDelta) {
                float targetAngle = terminalBlock.Angle + (float)angleDelta;
                Unlock();
                if(targetAngle > AngleLimit) targetAngle = AngleLimit;
                else if(targetAngle < -AngleLimit) targetAngle = -AngleLimit;
                if(targetAngle < terminalBlock.Angle) { 
                    terminalBlock.LowerLimitRad = targetAngle;
                    terminalBlock.UpperLimitRad = terminalBlock.Angle;
                }
                else {
                    terminalBlock.UpperLimitRad = targetAngle;
                    terminalBlock.LowerLimitRad = terminalBlock.Angle;
                }
                terminalBlock.TargetVelocityRPM = Math.Sign(targetAngle - terminalBlock.Angle);
            }
        }
        Vector3D target;
        readonly Rotor rotor;
        readonly Hinge hinge;
        readonly IMyTextPanel lcd;
        int timesGenerated;
        readonly RotationHelper rhInstance = new RotationHelper();

        readonly MyCommandLine _commandLine = new MyCommandLine();
        public Program() {
            var blocks = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocks(blocks);
            rotor = new Rotor(GetBlock<IMyMotorStator>("Rotor mechanics", blocks));
            hinge = new Hinge(GetBlock<IMyMotorStator>("Hinge mechanics", blocks));
            lcd = GetBlock<IMyTextPanel>("", blocks);

            target = RandomNormalVector();
            lcd.ContentType = ContentType.TEXT_AND_IMAGE;
            lcd.WriteText(ToGPSString(target, $"AlignmentVector#{timesGenerated}", "#4dff88"));
            Runtime.UpdateFrequency = UpdateFrequency.Update1;
        }
        public void Main(string argument, UpdateType updateSource) {
            if(_commandLine.TryParse(argument)) {
                switch(_commandLine.Argument(0)) {
                    case "newVector":
                        timesGenerated++;
                        target = RandomNormalVector();
                        lcd.WriteText(ToGPSString(target, $"AlignmentVector#{timesGenerated}", "#4dff88"), true);
                        break;
                    case "align":
                        Vector3D hingeForwardOrBackward = hinge.terminalBlock.WorldMatrix.Forward.Dot(target) > hinge.terminalBlock.WorldMatrix.Backward.Dot(target) ?
                            hinge.terminalBlock.WorldMatrix.Forward : hinge.terminalBlock.WorldMatrix.Backward;
                        double rotorRotationAngle = rotor.AlignToVector(rhInstance, hingeForwardOrBackward, target);
                        rhInstance.GenerateRotatedNormalizedVectorsAroundAxisByAngle(target, rotor.LocalRotationAxis, rotorRotationAngle);
                        Vector3D vecToAlign = hinge.HingeFacing;
                        Vector3D rotatedTargetVector = rhInstance.RotatedVectorClockwise.Dot(hingeForwardOrBackward) >
                            rhInstance.RotatedVectorCounterClockwise.Dot(hingeForwardOrBackward) ?
                            rhInstance.RotatedVectorClockwise : rhInstance.RotatedVectorCounterClockwise;
                        hinge.AlignToVector(rhInstance, vecToAlign, rotatedTargetVector,
                                rhInstance.NormalizedVectorProjectedOntoPlane(hinge.LocalRotationAxis, rotor.LocalRotationAxis));
                        Echo($"rotorRotationAngle: {rotorRotationAngle}\n" +
                            $"HingeFacing.Dot(target): {hinge.HingeFacing.Dot(target)}\n");
                        break;
                    case "lock":
                        rotor.Lock();
                        hinge.Lock();
                        break;
                }
            }
            else {
                Vector3D hingeForwardOrBackward = hinge.terminalBlock.WorldMatrix.Forward.Dot(target) > 
                    hinge.terminalBlock.WorldMatrix.Backward.Dot(target) ?
                    hinge.terminalBlock.WorldMatrix.Forward : hinge.terminalBlock.WorldMatrix.Backward;
                double rotorRotationAngle = rotor.AlignToVector(rhInstance, hingeForwardOrBackward, target);
                rhInstance.GenerateRotatedNormalizedVectorsAroundAxisByAngle(target, rotor.LocalRotationAxis, rotorRotationAngle);
                Vector3D vecToAlign = hinge.HingeFacing;
                Vector3D rotatedTargetVector = rhInstance.RotatedVectorClockwise.Dot(hingeForwardOrBackward) >
                    rhInstance.RotatedVectorCounterClockwise.Dot(hingeForwardOrBackward) ?
                    rhInstance.RotatedVectorClockwise : rhInstance.RotatedVectorCounterClockwise;
                hinge.AlignToVector(rhInstance, vecToAlign, rotatedTargetVector,
                        rhInstance.NormalizedVectorProjectedOntoPlane(hinge.LocalRotationAxis, rotor.LocalRotationAxis));
                Echo($"rotorRotationAngle: {rotorRotationAngle}\n" +
                    $"HingeFacing.Dot(target): {hinge.HingeFacing.Dot(target)}\n");
                if(rhInstance.IsAlignedWithNormalizedTargetVector(target, hinge.HingeFacing, Rotor.ALIGNMENT_PRECISION_THRESHOLD)) {
                    rotor.Lock();
                    hinge.Lock();
                }
            }
        }
        public Vector3D RandomNormalVector() {
            var random = new Random();
            return Vector3D.Normalize(new Vector3D(random.Next(-10, 10), random.Next(0, 10), random.Next(-10, 10)));
        }
        public string ToGPSString(Vector3D vec, string coordName, string colorHex) {
            vec.Normalize();
            vec *= Math.Pow(10, 12);
            return $"GPS:{coordName}:{vec.X}:{vec.Y}:{vec.Z}:{colorHex}:\n";
        }
        class MyClassTest {
            Vector3D[] vecArray;
            public string DoSomething() {
                return $"{vecArray}\n{vecArray is object}";
            }
        }
        void Main(string argument) {
            var list = new List<IMySolarPanel>();
            GridTerminalSystem.GetBlocksOfType(list);
            var myTestBlock = list.Find(block => MyIni.HasSection(block.CustomData, "test")) ?? list[0];
            Echo($"{myTestBlock.CustomName}");
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