// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Snaps the splats onto slices, a grid or a lattice and fades those too far from
    // one, or leaves them as a black surface (matte). Mirrors StyleQuantize in GaussianStyleQuantize.hlsl.
    [Serializable]
    public sealed class GaussianStyleQuantizeBlock : GaussianStyleBlock<GaussianStyleQuantizeBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float mode;
            public float spacing;
            public float band;
            public Vector3 sliceNormal;
            public float sliceOffset;
            public float matte;
            public Vector3 pad;
        }

        [SerializeField] private GaussianSplatStyleQuantize mode = GaussianSplatStyleQuantize.Slices;
        [Tooltip("Distance between slices, grid points or lattice planes, in world units")]
        [SerializeField, Min(0.001f)] private float spacing = 0.03f;
        [Tooltip("Fraction of the spacing kept around each slice, grid point or lattice plane; splats farther out fade away. 1 keeps every splat and only snaps. On a lattice it sets how thick the lines are before snapping")]
        [SerializeField, Range(0.01f, 1f)] private float band = 1f;
        [Tooltip("World direction the slices are stacked along: up gives horizontal scan lines")]
        [SerializeField] private Vector3 sliceNormal = Vector3.up;
        [Tooltip("Shifts the slices along their normal, in world units; animate it to scroll them")]
        [SerializeField] private float sliceOffset;
        [Tooltip("Splats outside the band stay where they are as black splats instead of fading away, so the surface still hides what is behind it and only the nearest lines show. Needs a Blend block that is not fully additive: added light hides nothing")]
        [SerializeField] private bool matte;

        public GaussianSplatStyleQuantize Mode => mode;
        public float Spacing => spacing;

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            mode = (uint)mode,
            spacing = spacing,
            // World units around a slice, grid point or lattice plane that stay
            band = band * spacing * 0.5f,
            sliceNormal = sliceNormal.sqrMagnitude > 1e-8f ? sliceNormal.normalized : Vector3.up,
            sliceOffset = sliceOffset,
            matte = matte ? 1u : 0u,
        };

        // The farthest a splat is snapped
        internal override float Reach(float maxSpeed, float maxSegment) =>
            mode == GaussianSplatStyleQuantize.Grid ? spacing * 0.5f * Mathf.Sqrt(3f) : spacing * 0.5f;
    }
}
