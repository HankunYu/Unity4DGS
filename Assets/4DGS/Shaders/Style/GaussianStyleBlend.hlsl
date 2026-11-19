// SPDX-License-Identifier: MIT
// Blend block (GaussianStyleBlendBlock): how the splats blend with what is
// behind them. One choice per style rather than a hook: the quad shader
// takes the additivity from the draw's GaussianSplatStyleRange, and
// occlusion culling (SplatUtilities.compute) reads the slot.
#ifndef GAUSSIAN_STYLE_BLEND_HLSL
#define GAUSSIAN_STYLE_BLEND_HLSL

struct StyleBlend
{
    bool on;
    float additivity;       // at full weight, scaled per splat by its style weight
    bool occludes;          // the style's additive splats still occlude
};
#define STYLE_BLEND_ROWS 1
float4 _StyleBlend[MAX_STYLES * STYLE_BLEND_ROWS];

// Mirrors GaussianStyleBlendBlock.Data
StyleBlend LoadStyleBlend(uint slot)
{
    float4 r0 = _StyleBlend[slot * STYLE_BLEND_ROWS];
    StyleBlend b;
    b.on = r0.x > 0.5;
    b.additivity = r0.y;
    b.occludes = r0.z > 0.5;
    return b;
}

#endif // GAUSSIAN_STYLE_BLEND_HLSL
