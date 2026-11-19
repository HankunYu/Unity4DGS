// SPDX-License-Identifier: MIT

using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // The color blocks of a style set, with their ramps: one row per slot
    // of a texture the shaders sample (_StyleRampTex)
    internal sealed class GaussianStyleColorChannel : GaussianStyleChannel<GaussianStyleColorBlock, GaussianStyleColorBlock.Data>
    {
        private static readonly int StyleRampTex = Shader.PropertyToID("_StyleRampTex");

        private readonly GaussianStyleColorBlock[] _blocks = new GaussianStyleColorBlock[GaussianStyleSet.MaxStyles];
        // The block and ramp version each row was baked from
        private readonly GaussianStyleColorBlock[] _bakedBlocks = new GaussianStyleColorBlock[GaussianStyleSet.MaxStyles];
        private readonly int[] _bakedVersions = new int[GaussianStyleSet.MaxStyles];
        private readonly Color[] _pixels = new Color[GaussianStyleColorBlock.RampResolution];
        private Texture2D _ramps;

        public GaussianStyleColorChannel() : base("StyleColor")
        {
        }

        public override void Write(int slot, GaussianSplatStyle style)
        {
            base.Write(slot, style);
            _blocks[slot] = style != null && style.TryGet(out GaussianStyleColorBlock block) ? block : null;
        }

        public override void Bind(CommandBuffer cmb, ComputeShader cs, int kernelIndex, bool active)
        {
            base.Bind(cmb, cs, kernelIndex, active);
            // The kernel samples the ramps whether or not a style is set
            if (!active)
            {
                cmb.SetComputeTextureParam(cs, kernelIndex, StyleRampTex, Texture2D.whiteTexture);
                return;
            }

            BakeRamps();
            cmb.SetComputeTextureParam(cs, kernelIndex, StyleRampTex, _ramps);
        }

        public override void Dispose()
        {
            if (_ramps != null)
                Object.DestroyImmediate(_ramps);
            _ramps = null;
        }

        // Rebakes the rows whose block or ramp changed
        private void BakeRamps()
        {
            if (_ramps == null)
            {
                // Linear: splat colors are gamma space at this stage and the
                // values go in unconverted
                _ramps = new Texture2D(GaussianStyleColorBlock.RampResolution, GaussianStyleSet.MaxStyles, TextureFormat.RGBAHalf, false, true)
                {
                    name = "GaussianStyleRamps",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                System.Array.Clear(_bakedBlocks, 0, _bakedBlocks.Length);
            }

            bool changed = false;
            for (int i = 0; i < _blocks.Length; i++)
            {
                GaussianStyleColorBlock block = _blocks[i];
                if (block == null || (_bakedBlocks[i] == block && _bakedVersions[i] == block.RampVersion))
                    continue;
                block.BakeRamp(_pixels);
                _ramps.SetPixels(0, i, GaussianStyleColorBlock.RampResolution, 1, _pixels);
                _bakedBlocks[i] = block;
                _bakedVersions[i] = block.RampVersion;
                changed = true;
            }
            if (changed)
                _ramps.Apply(false);
        }
    }
}
