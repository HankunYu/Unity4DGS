// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Applies non-linear spatial distortion to splats within the volume.
    /// Supports twist, bend, spherize, and pinch deformations.
    /// </summary>
    public class WarpModifier : GaussianAnimModifier
    {
        public enum WarpType
        {
            Twist = 0,
            Bend = 1,
            Spherize = 2,
            Pinch = 3
        }

        [Tooltip("Type of spatial distortion")]
        public WarpType warpType = WarpType.Twist;

        [Tooltip("Distortion axis (auto-normalized on GPU)")]
        public Vector3 axis = Vector3.up;

        [Range(-5f, 5f)]
        [Tooltip("Distortion intensity")]
        public float strength = 0f;

        [Tooltip("Center point of distortion relative to volume local space")]
        public Vector3 center = Vector3.zero;

        public override int ModifierType => TypeWarp;

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            Vector3 ax = axis.normalized;
            // p0: axis xyz, strength
            p0 = new Vector4(ax.x, ax.y, ax.z, strength);
            // p1: center xyz, warpType
            p1 = new Vector4(center.x, center.y, center.z, (float)warpType);
            // p2: time, unused
            p2 = new Vector4(time, 0, 0, 0);
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(axis.x, axis.y, axis.z, strength);
            p1 = new Vector4(center.x, center.y, center.z, (float)warpType);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            axis = new Vector3(p0.x, p0.y, p0.z);
            strength = p0.w;
            center = new Vector3(p1.x, p1.y, p1.z);
            warpType = (WarpType)Mathf.RoundToInt(p1.w);
        }
    }
}
