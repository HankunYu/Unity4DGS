// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // The splat's base color and brightness: its own color or one from a
    // ramp, looked up by its luminance, a world-space measure or one of the
    // style's masks. Mirrors StyleColor in GaussianStyleColor.hlsl.
    [Serializable]
    public sealed class GaussianStyleColorBlock : GaussianStyleBlock<GaussianStyleColorBlock.Data>
    {
        internal const int RampResolution = 256;

        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float source;
            public float input;
            public float blend;
            public float rangeStart;
            public float inverseRange;
            public float keepLuminance;
            public float intensity;
            public Vector3 axis;
            public float pad;
        }

        [SerializeField] private GaussianSplatStyleColorSource source = GaussianSplatStyleColorSource.Original;
        [Tooltip("Base color across the ramp input, used when the source is Ramp")]
        [SerializeField] private Gradient ramp = new()
        {
            colorKeys = new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(Color.white, 1f) },
            alphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) },
        };
        [SerializeField] private GaussianSplatStyleRampInput input = GaussianSplatStyleRampInput.Luminance;
        [Tooltip("Start of the ramp in world units, for the Depth, World Axis and Volume Distance inputs: a distance from the camera or from the volume's centre, or a position along the axis")]
        [SerializeField] private float near = 0.5f;
        [Tooltip("End of the ramp in world units, for the Depth, World Axis and Volume Distance inputs")]
        [SerializeField] private float far = 10f;
        [Tooltip("World direction the ramp runs along, for the World Axis input: up colors by height")]
        [SerializeField] private Vector3 axis = Vector3.up;
        [Tooltip("How far the ramp color replaces the splat's own color: 0 keeps the splat's color, 1 takes the ramp's")]
        [SerializeField, Range(0f, 1f)] private float blend = 1f;
        [Tooltip("Scales the ramp color by the brightness of the splat's own color, so the ramp sets the hue and the splat keeps its light and dark")]
        [SerializeField] private bool keepLuminance;
        [Tooltip("Brightness multiplier. Above 1 drives values past white, for bloom to pick up")]
        [SerializeField, Range(0f, 8f)] private float intensity = 1f;

        // Counts the changes to the ramp, for what keeps a baked copy of it
        [NonSerialized] private int _rampVersion;

        public GaussianSplatStyleColorSource Source => source;
        public GaussianSplatStyleRampInput Input => input;
        public float Intensity => intensity;

        internal int RampVersion => _rampVersion;

        // Call after changing the ramp gradient from script
        public void MarkRampDirty() => _rampVersion++;

        // The ramp as a row of RampResolution texels, holding the gradient's
        // values unconverted: splat colors are gamma space at this stage
        internal void BakeRamp(Color[] pixels)
        {
            for (int i = 0; i < RampResolution; i++)
                pixels[i] = ramp.Evaluate(i / (RampResolution - 1f));
        }

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            source = (uint)source,
            input = (uint)input,
            blend = blend,
            rangeStart = near,
            inverseRange = 1f / Mathf.Max(far - near, 1e-4f),
            keepLuminance = keepLuminance ? 1f : 0f,
            intensity = intensity,
            axis = axis.sqrMagnitude > 1e-8f ? axis.normalized : Vector3.up,
        };

        internal override void Validate()
        {
            far = Mathf.Max(far, near + 0.01f);
            _rampVersion++;
        }
    }
}
