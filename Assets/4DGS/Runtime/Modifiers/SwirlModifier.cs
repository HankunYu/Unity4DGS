// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Applies a vortex-like rotation inside the owning animation volume.
    /// Axis and center are authored in volume local space so the swirl follows
    /// the volume transform instead of a fixed world-space pivot.
    /// </summary>
    public class SwirlModifier : GaussianAnimModifier
    {
        [Tooltip("Rotation axis in volume local space (auto-normalized on GPU)")]
        public Vector3 axis = Vector3.forward;

        [Tooltip("Center of the swirl in volume local space. (0,0,0) is the volume center.")]
        public Vector3 center = Vector3.zero;

        [Range(-5f, 5f)]
        [Tooltip("Angular intensity. Sign controls clockwise vs counter-clockwise spin.")]
        public float strength = 1f;

        [Range(0.01f, 1.5f)]
        [Tooltip("Swirl radius in normalized volume local space. 0.5 reaches the volume boundary.")]
        public float radius = 0.5f;

        [Range(0f, 5f)]
        [Tooltip("Animation speed")]
        public float speed = 0.5f;

        [Range(0f, 1f)]
        [Tooltip("Edge feather inside the radius, in normalized local units.")]
        public float softness = 0.15f;

        public override int ModifierType => TypeSwirl;

        public override string[] GetParamLabels() => new[]
        {
            "Axis X", "Axis Y", "Axis Z", "Strength",
            "Center X", "Center Y", "Center Z", "Radius",
            "Speed", "Softness", "Time", "",
            "", "", "", ""
        };

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            Vector3 ax = axis.normalized;
            p0 = new Vector4(ax.x, ax.y, ax.z, strength);
            p1 = new Vector4(center.x, center.y, center.z, radius);
            p2 = new Vector4(speed, softness, time, 0);
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(axis.x, axis.y, axis.z, strength);
            p1 = new Vector4(center.x, center.y, center.z, radius);
            p2 = new Vector4(speed, softness, 0, 0);
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            axis = new Vector3(p0.x, p0.y, p0.z);
            strength = p0.w;
            center = new Vector3(p1.x, p1.y, p1.z);
            radius = p1.w;
            speed = p2.x;
            softness = p2.y;
        }
    }
}
