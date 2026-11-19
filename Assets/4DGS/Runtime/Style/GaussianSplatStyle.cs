using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // A stylized look for splats, applied by a GaussianStyleVolume under a
    // GaussianStyler and blended in by the volume's weight. The look is the
    // sum of the style's blocks (GaussianStyleBlock), each one effect: add
    // the ones the look needs. Applied per splat in the view-data pass, so it
    // works on every draw path, in stereo and in Direct composite mode.
    // Styles draw in the Splats and PointCloud render modes.
    [CreateAssetMenu(menuName = "Gaussian Splatting/Style", fileName = "SplatStyle", order = 2)]
    public class GaussianSplatStyle : ScriptableObject
    {
        [SerializeReference] private List<GaussianStyleBlock> blocks = new();

        public IReadOnlyList<GaussianStyleBlock> Blocks => blocks;

        // The style's enabled block of this type
        public bool TryGet<TBlock>(out TBlock block) where TBlock : GaussianStyleBlock
        {
            foreach (GaussianStyleBlock candidate in blocks)
            {
                if (candidate is TBlock found && found.Enabled)
                {
                    block = found;
                    return true;
                }
            }
            block = null;
            return false;
        }

        // The style's block of this type, enabled or not
        public TBlock Find<TBlock>() where TBlock : GaussianStyleBlock => (TBlock)Find(typeof(TBlock));

        public GaussianStyleBlock Find(Type type)
        {
            foreach (GaussianStyleBlock candidate in blocks)
            {
                if (candidate != null && candidate.GetType() == type)
                    return candidate;
            }
            return null;
        }

        // Adds a block of this type with its default settings, or returns
        // the one the style has
        public TBlock Add<TBlock>() where TBlock : GaussianStyleBlock, new() => (TBlock)Add(typeof(TBlock));

        public GaussianStyleBlock Add(Type type)
        {
            GaussianStyleBlock block = Find(type);
            if (block == null)
            {
                block = (GaussianStyleBlock)Activator.CreateInstance(type);
                blocks.Add(block);
            }
            return block;
        }

        public bool Remove(Type type)
        {
            GaussianStyleBlock block = Find(type);
            if (block == null)
                return false;
            return blocks.Remove(block);
        }

        // How far what the style draws can lie from the splat's own
        // position, in world units: each block's reach, added up
        internal float Reach(float maxSpeed, float maxSegment)
        {
            float reach = 0f;
            foreach (GaussianStyleBlock block in blocks)
            {
                if (block != null && block.Enabled)
                    reach += block.Reach(maxSpeed, maxSegment);
            }
            return reach;
        }

        // The draw-side parameters of the style's slot
        internal void FillRange(ref GaussianSplatStyleRange range, int slot)
        {
            foreach (GaussianStyleBlock block in blocks)
            {
                if (block != null && block.Enabled)
                    block.FillRange(ref range, slot);
            }
        }

        // Call after changing a block's settings from script
        public void NotifyChanged()
        {
            foreach (GaussianStyleBlock block in blocks)
                block?.Validate();
        }

        private void OnValidate()
        {
            // A block whose type is gone loads as null; a duplicate can only
            // come from a hand-edited asset
            for (int i = blocks.Count - 1; i >= 0; i--)
            {
                if (blocks[i] == null || Find(blocks[i].GetType()) != blocks[i])
                    blocks.RemoveAt(i);
            }
            NotifyChanged();
        }
    }
}
