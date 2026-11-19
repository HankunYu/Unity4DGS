// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // A fraction of the splats flash now and then, each on its own clock.
    // Mirrors StyleGlint in GaussianStyleGlint.hlsl.
    [Serializable]
    public sealed class GaussianStyleGlintBlock : GaussianStyleBlock<GaussianStyleGlintBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float density;
            public float rate;
            public float intensity;
        }

        [Tooltip("Fraction of splats that flash now and then")]
        [SerializeField, Range(0f, 1f)] private float density = 0.1f;
        [Tooltip("Flashes per second for each glinting splat, on average")]
        [SerializeField, Range(0f, 10f)] private float rate = 1f;
        [Tooltip("Brightness added at the peak of a flash, as a multiple of the splat's color")]
        [SerializeField, Range(0f, 16f)] private float intensity = 4f;

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            density = density,
            rate = rate,
            intensity = intensity,
        };
    }
}
