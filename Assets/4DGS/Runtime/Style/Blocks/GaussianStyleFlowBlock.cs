// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Waves of light running through the splats along a direction, each
    // column of space with its own. Mirrors StyleFlow in
    // GaussianStyleFlow.hlsl.
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
            public Vector2 pad;
        }

        [Tooltip("How far brightness follows the waves: 0 leaves the splats alone, 1 lights only each wave and its fading tail")]
        [SerializeField, Range(0f, 1f)] private float amount = 1f;
        [Tooltip("World direction the waves run in")]
        [SerializeField] private Vector3 direction = Vector3.down;
        [Tooltip("How fast the waves run, in world units per second. Each column runs at its own share of it")]
        [SerializeField, Min(0f)] private float speed = 0.5f;
        [Tooltip("Length of the lit tail behind each wave, in world units")]
        [SerializeField, Min(0.001f)] private float length = 0.4f;
        [Tooltip("Distance from one wave to the next in a column, in world units. At or below the tail length the column never goes dark")]
        [SerializeField, Min(0.001f)] private float spacing = 0.8f;
        [Tooltip("Takes the column size and the step from the style's Quantize block, so each grid column carries its own waves and they light whole cells one after another. Without a Quantize block the two below apply")]
        [SerializeField] private bool alignToGrid;
        [Tooltip("Width of a column, in world units")]
        [SerializeField, Min(0.001f), GaussianStyleDisabledIf(nameof(alignToGrid))] private float columnSize = 0.03f;
        [Tooltip("Steps the waves advance in, in world units, instead of sliding the brightness smoothly. 0 runs smoothly")]
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
            return new Data
            {
                on = 1,
                amount = amount,
                speed = speed,
                spacing = spacing,
                direction = direction.sqrMagnitude > 1e-8f ? direction.normalized : Vector3.down,
                columnsPerUnit = 1f / column,
                tailLength = length,
                stride = stride,
            };
        }
    }
}
