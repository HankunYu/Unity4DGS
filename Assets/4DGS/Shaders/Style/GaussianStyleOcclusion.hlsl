// SPDX-License-Identifier: MIT
// Occlusion block (GaussianStyleOcclusionBlock): darkens splats by their
// baked occlusion.
#ifndef GAUSSIAN_STYLE_OCCLUSION_HLSL
#define GAUSSIAN_STYLE_OCCLUSION_HLSL

struct StyleOcclusion
{
    bool on;
    float amount;           // how far the occlusion darkens
    float contrast;
};
#define STYLE_OCCLUSION_ROWS 1
float4 _StyleOcclusion[MAX_STYLES * STYLE_OCCLUSION_ROWS];

// Mirrors GaussianStyleOcclusionBlock.Data
StyleOcclusion LoadStyleOcclusion(uint slot)
{
    float4 r0 = _StyleOcclusion[slot * STYLE_OCCLUSION_ROWS];
    StyleOcclusion b;
    b.on = r0.x > 0.5;
    b.amount = r0.y;
    b.contrast = r0.z;
    return b;
}

// Baked occlusion of the splats (GaussianMeshBinding): a byte per splat, 0
// covered to 255 open, four to a uint. The splats' renderer binds it.
StructuredBuffer<uint> _SplatOcclusion;
uint _SplatOcclusionValid;

// How open a splat's surroundings are, from its baked occlusion: 0 covered,
// 1 open, and 1 for splats without one. Needs no block: the color block's
// Occlusion ramp input reads it too.
float StyleOcclusionTerm(uint idx)
{
    if (_SplatOcclusionValid == 0)
        return 1.0;
    uint word = _SplatOcclusion[idx >> 2];
    return ((word >> ((idx & 3) * 8)) & 255) * (1.0 / 255.0);
}

void StyleOcclusionMask(StyleContext c, inout float mask)
{
    StyleOcclusion b = LoadStyleOcclusion(c.slot);
    if (!b.on)
        return;
    mask *= lerp(1.0, pow(StyleOcclusionTerm(c.idx), b.contrast), b.amount);
}

#endif // GAUSSIAN_STYLE_OCCLUSION_HLSL
