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
using PXMixins_PrincipleAxis;

namespace PXMixins_RotationHelper {
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
        public void ClearCache() {
            RotatedVectorClockwise = Vector3D.Zero;
            RotatedVectorCounterClockwise = Vector3D.Zero;
        }
    }
}
