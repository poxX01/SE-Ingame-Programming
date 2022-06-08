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
    public class Rotor {
        private const float ANGLE_LIMIT = (float)Math.PI * 2;
        public const float ALIGNMENT_PRECISION_THRESHOLD = 0.0000001f;
        public readonly IMyMotorStator terminalBlock;
        private static readonly Vector3I blockPosOnTop = new Vector3I(0, 1, 0);
        public virtual Vector3I BlockPositionOnTop {
            get { return blockPosOnTop; }
        }
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
        private static readonly Vector3I blockPosOnTop = new Vector3I(-1, 0, 0);
        public override Vector3I BlockPositionOnTop {
            get { return blockPosOnTop; }
        }
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
}
