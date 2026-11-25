// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Waves of light running through the splats along a direction, each
    // column with its own: columns of world space, strips of the surface or
    // strips of the mesh's UV space (GaussianSplatStyleFlowSpace). Mirrors
    // StyleFlow in GaussianStyleFlow.hlsl.
    [Serializable]
    public sealed class GaussianStyleFlowBlock : GaussianStyleBlock<GaussianStyleFlowBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float amount;
            public float speed;
            public float spacing;
            public Vector3 direction;
            public float columnsPerUnit;
            public float tailLength;
            public float stride;
            public float space;
            public float pad;
        }

        [Tooltip("How far brightness follows the waves: 0 leaves the splats alone, 1 lights only each wave and its fading tail")]
        [SerializeField, Range(0f, 1f)] private float amount = 1f;
        [Tooltip("What the waves run through: straight columns of world space, strips lying in the splats' surface, or strips in the UV space of the mesh they were sampled from (a .meshsplat with Store UVs). The lengths below are world units, or UV units in UV space")]
        [SerializeField] private GaussianSplatStyleFlowSpace space = GaussianSplatStyleFlowSpace.World;
        [Tooltip("Direction the waves run in: a world direction, or in UV space its X and Y as U and V")]
        [SerializeField] private Vector3 direction = Vector3.down;
        [Tooltip("How fast the waves run, in units per second. Each column runs at its own share of it")]
        [SerializeField, Min(0f)] private float speed = 0.5f;
        [Tooltip("Length of the lit tail behind each wave")]
        [SerializeField, Min(0.001f)] private float length = 0.4f;
        [Tooltip("Distance from one wave to the next in a column. At or below the tail length the column never goes dark")]
        [SerializeField, Min(0.001f)] private float spacing = 0.8f;
        [Tooltip("Takes the column size and the step from the style's Quantize block, so each grid column carries its own waves and they light whole cells one after another. Without a Quantize block the two below apply")]
        [SerializeField] private bool alignToGrid;
        [Tooltip("Width of a column")]
        [SerializeField, Min(0.001f), GaussianStyleDisabledIf(nameof(alignToGrid))] private float columnSize = 0.03f;
        [Tooltip("Steps the waves advance in, instead of sliding the brightness smoothly. 0 runs smoothly")]
        [SerializeField, Min(0f), GaussianStyleDisabledIf(nameof(alignToGrid))] private float step;

        public bool AlignToGrid => alignToGrid;

        internal override Data Pack(GaussianSplatStyle style)
        {
            float column = columnSize;
            float stride = step;
            if (alignToGrid && style.TryGet(out GaussianStyleQuantizeBlock quantize))
            {
                column = quantize.Spacing;
                stride = quantize.Spacing;
            }
            // UV space takes the direction's X and Y as U and V
            Vector3 along = space == GaussianSplatStyleFlowSpace.Uv ? new Vector3(direction.x, direction.y, 0f) : direction;
            return new Data
            {
                on = 1,
                amount = amount,
                speed = speed,
                spacing = spacing,
                direction = along.sqrMagnitude > 1e-8f ? along.normalized : Vector3.down,
                columnsPerUnit = 1f / column,
                tailLength = length,
                stride = stride,
                space = (float)space,
            };
        }
    }
}
