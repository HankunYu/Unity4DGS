// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // The splat's edge: a grazing-angle term, optionally broken up by a
    // noise, that dims surfaces facing the camera, adds a Fresnel glow, and
    // fades and spills the splats at silhouettes. Mirrors StyleRim in
    // GaussianStyleRim.hlsl.
    [Serializable]
    public sealed class GaussianStyleRimBlock : GaussianStyleBlock<GaussianStyleRimBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float power;
            public float amount;
            public float glow;
            public Vector3 color;
            public float sourceTint;
            public float noise;
            public float noiseFrequency;
            public float noiseSpeed;
            public float noiseGrain;
            public float fade;
            public float spill;
            public Vector2 pad;
        }

        [Header("Edge")]
        [Tooltip("Sharpness of the rim: higher keeps it closer to silhouettes. The light, glow, fade and spill below all follow it")]
        [SerializeField, Range(0.5f, 8f)] private float power = 2f;
        [Tooltip("How far a noise breaks up the rim: 0 keeps it an even band, 1 lets it come and go in patches")]
        [SerializeField, Range(0f, 1f)] private float noise;
        [Tooltip("Size of the noise patches, in the asset's units. The noise sits on each splat's rest position as stored in the asset, so it stays on a body a mesh driver deforms")]
        [SerializeField, Min(0.001f)] private float noiseSize = 0.05f;
        [Tooltip("How fast the noise patches drift and change, in patch sizes per second")]
        [SerializeField, Min(0f)] private float noiseSpeed = 0.3f;
        [Tooltip("Per-splat randomness in where the patches end: 0 keeps their edges smooth, 1 breaks them into scattered grains")]
        [SerializeField, Range(0f, 1f)] private float noiseGrain;

        [Header("Light")]
        [Tooltip("How far brightness follows the rim: 0 ignores it, 1 leaves surfaces facing the camera dark and lights only grazing ones")]
        [SerializeField, Range(0f, 1f)] private float amount;
        [Tooltip("Light added at grazing angles, a Fresnel edge. Unlike the amount, which only darkens surfaces facing the camera, it lifts edges past the base color")]
        [SerializeField, Range(0f, 8f)] private float glow = 1f;
        [Tooltip("Color of the glow, multiplied with the splat's own color by the source tint")]
        [SerializeField] private Color color = Color.white;
        [Tooltip("How far the glow takes each splat's own color, lifted to full brightness with its hue kept: 0 glows in the color alone, 1 in the splat's color times the color")]
        [SerializeField, Range(0f, 1f)] private float sourceTint = 1f;

        [Header("Scatter")]
        [Tooltip("How far the rim fades splats out, so silhouettes thin into scattered points instead of a solid line")]
        [SerializeField, Range(0f, 1f)] private float fade;
        [Tooltip("Farthest the rim pushes splats out along their surface normal (mesh splats' local +Z), in world units. Each splat goes its own random share of it, most a little and a few the whole way, so silhouettes spill a scatter of points")]
        [SerializeField, Min(0f)] private float spill;

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            power = power,
            amount = amount,
            glow = glow,
            color = new Vector3(color.r, color.g, color.b),
            sourceTint = sourceTint,
            noise = noise,
            noiseFrequency = 1f / noiseSize,
            noiseSpeed = noiseSpeed,
            noiseGrain = noiseGrain,
            fade = fade,
            spill = spill,
        };

        internal override float Reach(float maxSpeed, float maxSegment) => spill;
    }
}
