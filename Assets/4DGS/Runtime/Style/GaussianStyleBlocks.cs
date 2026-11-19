// SPDX-License-Identifier: MIT

namespace GaussianSplatting.Runtime
{
    // The kinds of style block there are. A new block is registered here,
    // and its hooks called in GaussianStyle.hlsl.
    internal static class GaussianStyleBlocks
    {
        // A set of channels for one GaussianStyleSet, one per kind of block
        public static GaussianStyleChannel[] CreateChannels() => new GaussianStyleChannel[]
        {
            new GaussianStyleColorChannel(),
            new GaussianStyleChannel<GaussianStyleRimBlock, GaussianStyleRimBlock.Data>("StyleRim"),
            new GaussianStyleChannel<GaussianStyleGlintBlock, GaussianStyleGlintBlock.Data>("StyleGlint"),
            new GaussianStyleChannel<GaussianStyleFlowBlock, GaussianStyleFlowBlock.Data>("StyleFlow"),
            new GaussianStyleChannel<GaussianStyleOcclusionBlock, GaussianStyleOcclusionBlock.Data>("StyleOcclusion"),
            new GaussianStyleChannel<GaussianStyleDepthBlock, GaussianStyleDepthBlock.Data>("StyleDepth"),
            new GaussianStyleChannel<GaussianStyleThinningBlock, GaussianStyleThinningBlock.Data>("StyleThinning"),
            new GaussianStyleChannel<GaussianStyleQuantizeBlock, GaussianStyleQuantizeBlock.Data>("StyleQuantize"),
            new GaussianStyleChannel<GaussianStyleShapeBlock, GaussianStyleShapeBlock.Data>("StyleShape"),
            new GaussianStyleChannel<GaussianStyleBlendBlock, GaussianStyleBlendBlock.Data>("StyleBlend"),
        };
    }
}
