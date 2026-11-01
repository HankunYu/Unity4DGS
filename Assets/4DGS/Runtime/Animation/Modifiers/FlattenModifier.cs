// SPDX-License-Identifier: MIT
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Lays splats flat onto the volume's local XY plane (through its centre,
    /// facing its local Z), so a model reads as a picture of itself: at full
    /// amount every splat sits on the plane, and easing the amount back to 0
    /// inflates it into depth again. Rotate the volume to choose the plane;
    /// an Unbounded volume flattens the whole renderer.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Animation/Modifiers/Flatten")]
    public class FlattenModifier : GaussianAnimModifier
    {
        [Range(0f, 1f)]
        [Tooltip("How far splats move onto the plane: 0 leaves them where they are, 1 lays them flat")]
        public float amount = 1f;

        public override int ModifierType => TypeFlatten;

        public override string[] GetParamLabels() => new[]
        {
            "Amount", "", "", "",
            "", "", "", "",
            "", "", "", "",
            "", "", "", ""
        };

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            // p0: amount, unused
            p0 = new Vector4(amount, 0f, 0f, 0f);
            p1 = Vector4.zero;
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(amount, 0f, 0f, 0f);
            p1 = Vector4.zero;
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            amount = p0.x;
        }
    }
}
