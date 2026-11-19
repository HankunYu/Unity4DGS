// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // A depth mask: white on the side of its reference that faces the
    // camera, black behind it, so it follows the camera and whatever the
    // reference follows. It darkens the splats by its amount, and is what
    // the color block's Volume Depth ramp input reads, at any amount.
    // Mirrors StyleDepth in GaussianStyleDepth.hlsl.
    [Serializable]
    public sealed class GaussianStyleDepthBlock : GaussianStyleBlock<GaussianStyleDepthBlock.Data>
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Data
        {
            public float on;
            public float amount;
            public float inverseRange;
            public float offset;
            public float reference;
            public Vector3 pad;
        }

        [Tooltip("How far the mask darkens the splat. At 0 the mask only feeds the color block's Volume Depth ramp input")]
        [SerializeField, Range(0f, 1f)] private float amount = 1f;
        [Tooltip("Depth the mask takes to go from white to black, in world units, centred on the reference: about the depth of a figure lights its near side and darkens its far side")]
        [SerializeField, Min(0.01f)] private float range = 0.6f;
        [Tooltip("Moves the mask's middle away from the camera (towards it when negative), in world units; animate it to sweep the mask through the volume")]
        [SerializeField] private float offset;
        [Tooltip("What depth is measured against: the centre of the style volume, one near and far side for all it holds, or the centre of the mesh each splat sits on (a mesh splat with a Mesh Driver), so every figure and prop is lit on its own near side")]
        [SerializeField] private GaussianSplatStyleDepthReference reference = GaussianSplatStyleDepthReference.VolumeCentre;

        internal override Data Pack(GaussianSplatStyle style) => new()
        {
            on = 1,
            amount = amount,
            inverseRange = 1f / range,
            offset = offset,
            reference = (uint)reference,
        };
    }
}
