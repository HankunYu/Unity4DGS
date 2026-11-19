// SPDX-License-Identifier: MIT

using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // One effect of a GaussianSplatStyle: its settings, and what it gives
    // the renderer. A style holds at most one block of each type, and a
    // block it does not hold is an effect it does not have. Blocks with GPU
    // data derive from GaussianStyleBlock<TData>. To add an effect, write a
    // block and its HLSL file (Shaders/Style), register the block's channel
    // in GaussianStyleBlocks and call its hooks in GaussianStyle.hlsl.
    [Serializable]
    public abstract class GaussianStyleBlock
    {
        [SerializeField, HideInInspector] private bool enabled = true;

        // A disabled block counts as absent
        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        // How far what the block draws can lie from the splat's own
        // position, in world units. maxSpeed and maxSegment bound the
        // renderer's velocity and segment sources (0 without one).
        internal virtual float Reach(float maxSpeed, float maxSegment) => 0f;

        // The block's share of its slot's draw-side parameters
        internal virtual void FillRange(ref GaussianSplatStyleRange range, int slot)
        {
        }

        // Fixes the block's own fields up after an edit
        internal virtual void Validate()
        {
        }
    }

    // A block with GPU data. TData is what the block's HLSL file unpacks
    // (LoadStyleXxx): floats only, in rows of four with no vector crossing a
    // row, the first field `on`, and all zeros standing for a style without
    // the block.
    [Serializable]
    public abstract class GaussianStyleBlock<TData> : GaussianStyleBlock where TData : unmanaged
    {
        // The block as its HLSL file reads it; the style gives access to the
        // blocks beside it
        internal abstract TData Pack(GaussianSplatStyle style);
    }
}
