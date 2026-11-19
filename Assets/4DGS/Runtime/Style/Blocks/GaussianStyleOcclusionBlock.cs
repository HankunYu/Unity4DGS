// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Darkens splats by their baked occlusion: creases, armpits and the
    // hollows under a chin or collar go dark, exposed surfaces stay. Needs a
    // .meshsplat imported with Bake Occlusion and its mesh driver; without
    // them nothing changes. Mirrors StyleOcclusion in
    // GaussianStyleOcclusion.hlsl.
    [Serializable]
    public sealed class GaussianStyleOcclusionBlock : GaussianStyleBlock<GaussianStyleOcclusionBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float amount;
            public float contrast;
            public float pad;
        }

        [Tooltip("How far the splat's baked occlusion darkens it")]
        [SerializeField, Range(0f, 1f)] private float amount = 1f;
        [Tooltip("Contrast of the mask: above 1 deepens and widens the dark, below 1 keeps only the deepest creases")]
        [SerializeField, Range(0.25f, 8f)] private float contrast = 1f;

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            amount = amount,
            contrast = contrast,
        };
    }
}
