// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Defines a spatial region (Box or Sphere) that controls which splats
    /// are affected by animation modifiers. Place this under a
    /// GaussianAnimator, adjust the transform to position/scale the volume,
    /// and add modifier components as siblings.
    /// Timelines drive Falloff and Trail Duration as channels.
    /// </summary>
    public class GaussianAnimVolume : GaussianVolume, IGaussianTimelineTarget
    {
        private static readonly string[] ChannelNames = { "Falloff", "Trail Duration" };

        [Range(0f, 5f)]
        [Tooltip("How long the effect lingers after the volume moves away (seconds). 0 = instant off.")]
        public float trailDuration = 0f;

        protected override Color GizmoColor => Color.cyan;

        int IGaussianTimelineTarget.ChannelCount => ChannelNames.Length;

        string IGaussianTimelineTarget.GetChannelName(int index) => ChannelNames[index];

        float IGaussianTimelineTarget.GetChannelValue(int index) => index == 0 ? falloff : trailDuration;

        void IGaussianTimelineTarget.SetChannelValue(int index, float value)
        {
            if (index == 0)
                falloff = Mathf.Max(0f, value);
            else
                trailDuration = Mathf.Max(0f, value);
        }

        float IGaussianTimelineTarget.BlendChannel(int index, float from, float to, float t) => Mathf.Lerp(from, to, t);

        /// <summary>
        /// GPU-uploadable data matching AnimVolumeData in the compute shader.
        /// </summary>
        public struct ShaderData
        {
            public Matrix4x4 worldToLocal;  // 64 bytes
            public Matrix4x4 localToWorld;  // 64 bytes
            public Vector4 shapeParams;     // x: type(0=box,1=sphere,2=unbounded), y: falloff, z: trail duration, w: reserved
            public Vector4 boundsSize;      // box half-extents from scale, or sphere radius
        }

        /// <summary>
        /// Collect all active modifier components on this GameObject.
        /// </summary>
        public GaussianAnimModifier[] GetModifiers()
        {
            return GetComponents<GaussianAnimModifier>();
        }

        /// <summary>
        /// Pack volume data for GPU upload. Uses the transform's scale as the volume size.
        /// For Box: half-extents = scale * 0.5
        /// For Sphere: radius = max component of scale * 0.5
        /// </summary>
        public ShaderData GetShaderData()
        {
            ShaderData sd = default;
            var tr = transform;
            sd.worldToLocal = tr.worldToLocalMatrix;
            sd.localToWorld = tr.localToWorldMatrix;
            sd.shapeParams = new Vector4((float)shape, falloff, trailDuration, 0);

            Vector3 scale = tr.lossyScale;
            if (shape == GaussianVolumeShape.Box)
            {
                // box half-extents: unit cube [-1,1] scaled by transform
                sd.boundsSize = new Vector4(scale.x * 0.5f, scale.y * 0.5f, scale.z * 0.5f, 0);
            }
            else
            {
                // sphere radius: use max axis
                float radius = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) * 0.5f;
                sd.boundsSize = new Vector4(radius, radius, radius, 0);
            }

            return sd;
        }
    }
}
