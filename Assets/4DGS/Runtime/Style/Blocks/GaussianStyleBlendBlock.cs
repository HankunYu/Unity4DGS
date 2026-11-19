// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // How the splats blend with what is behind them. Mirrors StyleBlend in
    // GaussianStyleBlend.hlsl.
    [Serializable]
    public sealed class GaussianStyleBlendBlock : GaussianStyleBlock<GaussianStyleBlendBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float additivity;
            public float occludes;
            public float pad;
        }

        [Tooltip("0 blends like ordinary splats, 1 adds light without occluding what is behind. Fully additive splats skip sorting")]
        [SerializeField, Range(0f, 1f)] private float additivity = 1f;
        [Tooltip("Additive splats still hide what lies behind them under the renderer's occlusion culling, so they glow like a solid surface instead of adding up with the layers behind (e.g. clothes over clothes)")]
        [SerializeField] private bool occludes;

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            additivity = additivity,
            occludes = occludes ? 1u : 0u,
        };

        internal override void FillRange(ref GaussianSplatStyleRange range, int slot)
        {
            range.additivity[slot] = additivity;
        }
    }
}
