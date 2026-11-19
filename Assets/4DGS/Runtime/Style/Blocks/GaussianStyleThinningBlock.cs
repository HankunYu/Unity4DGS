// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Drops a share of the splats, always the same ones. Mirrors
    // StyleThinning in GaussianStyleThinning.hlsl.
    [Serializable]
    public sealed class GaussianStyleThinningBlock : GaussianStyleBlock<GaussianStyleThinningBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float keepFraction;
            public float densityDistance;
            public float pad;
        }

        [Tooltip("Fraction of splats kept. The rest fade out with the style weight and stop costing anything at full weight")]
        [SerializeField, Range(0f, 1f)] private float keepFraction = 0.5f;
        [Tooltip("Camera distance (world units) the look is tuned at. Farther away, splats are thinned by the square of the distance ratio, so fixed-size dots and streaks keep the density and brightness on screen they have here instead of piling up into a blown-out blob. 0 turns it off")]
        [SerializeField, Min(0f)] private float densityDistance;

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            keepFraction = keepFraction,
            densityDistance = densityDistance,
        };
    }
}
