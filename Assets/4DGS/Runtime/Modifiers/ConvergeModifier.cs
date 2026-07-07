// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Pulls splats toward the volume center along multiple twisted spiral arms,
    /// creating a multi-vortex convergence effect.
    /// </summary>
    public class ConvergeModifier : GaussianAnimModifier
    {
        [Range(0f, 5f)]
        [Tooltip("How strongly splats are pulled toward the center")]
        public float convergence = 1f;

        [Range(0f, 10f)]
        [Tooltip("Tangential spiral strength — how much splats rotate as they converge")]
        public float spiralTwist = 3f;

        [Range(1, 8)]
        [Tooltip("Number of tornado arms")]
        public int arms = 4;

        [Range(0.05f, 2f)]
        [Tooltip("Width of each arm (higher = wider, softer arms)")]
        public float armWidth = 0.5f;

        [Range(0f, 10f)]
        [Tooltip("How much the arms curve and twist along their path")]
        public float armCurve = 2f;

        [Range(0f, 5f)]
        [Tooltip("Animation speed")]
        public float speed = 1f;

        public override int ModifierType => TypeConverge;

        public override string[] GetParamLabels() => new[]
        {
            "Convergence", "Spiral Twist", "Arms", "Arm Width",
            "Arm Curve", "Speed", "", "",
            "", "", "", "",
            "", "", "", ""
        };

        private float _phase;

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            _phase += AdvanceFillTime(time) * speed;
            p0 = new Vector4(convergence, spiralTwist, arms, armWidth);
            p1 = new Vector4(armCurve, speed, _phase, 0);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(convergence, spiralTwist, arms, armWidth);
            p1 = new Vector4(armCurve, speed, 0, 0);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            convergence = p0.x;
            spiralTwist = p0.y;
            arms = Mathf.RoundToInt(p0.z);
            armWidth = p0.w;
            armCurve = p1.x;
            speed = p1.y;
        }
    }
}
