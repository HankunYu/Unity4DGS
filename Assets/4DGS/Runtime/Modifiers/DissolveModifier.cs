// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Scatters splats outward from their original position (dissolve)
    /// or gathers them back. Useful for scene transitions, entity
    /// appearance/disappearance effects.
    /// </summary>
    public class DissolveModifier : GaussianAnimModifier
    {
        [Tooltip("Direction in which splats scatter (world space, auto-normalized on GPU)")]
        public Vector3 direction = Vector3.up;

        [Range(0f, 1f)]
        [Tooltip("Dissolve strength: 0 = fully gathered, 1 = fully scattered")]
        public float strength = 0f;

        [Range(0.1f, 10f)]
        [Tooltip("Noise frequency for per-splat random offset variation")]
        public float noiseScale = 1f;

        [Range(0f, 5f)]
        [Tooltip("Speed at which noise pattern evolves over time")]
        public float noiseSpeed = 1f;

        public override int ModifierType => TypeDissolve;

        public override string[] GetParamLabels() => new[]
        {
            "Direction X", "Direction Y", "Direction Z", "Strength",
            "Noise Scale", "Noise Speed", "", "",
            "", "", "", "",
            "", "", "", ""
        };

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            Vector3 dir = direction.normalized;
            // p0: direction xyz, strength
            p0 = new Vector4(dir.x, dir.y, dir.z, strength);
            // p1: noiseScale, noiseSpeed, time, unused
            p1 = new Vector4(noiseScale, noiseSpeed, time, 0);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(direction.x, direction.y, direction.z, strength);
            p1 = new Vector4(noiseScale, noiseSpeed, 0, 0);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            direction = new Vector3(p0.x, p0.y, p0.z);
            strength = p0.w;
            noiseScale = p1.x;
            noiseSpeed = p1.y;
        }

        public void SetStrength(float targetStrength)
        {
            strength = targetStrength;
        }
    }
}
