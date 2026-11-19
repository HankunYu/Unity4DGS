// SPDX-License-Identifier: MIT
// Styles (GaussianSplatStyle): which style a splat wears and how strongly
// (the strongest of its renderer's GaussianStyleVolumes, scaled by the
// GaussianStyler's weight), and what that style's blocks do to it. Shared by
// the view data pass (SplatUtilities.compute) and by GaussianParticleEmitter
// and GaussianGridLinks, which color their splats the way the source is
// drawn. GaussianStyleSet binds the data, one slot per distinct style.
//
// A block is a file in Style/ with its data (a struct, the float4 rows a
// GaussianStyleChannel binds for each slot, and the function unpacking
// them) and its hooks, called here in stages. Each hook leaves its
// accumulators alone when the slot's style lacks the block:
//   Displace(c, worldPos, keep)   moves the splat and drops it
//   Base(c, base)                 replaces its color
//   Light(c, gain, emission)      scales the light it gives, and adds to it
//   Mask(c, mask)                 darkens all of it, base and emission alike
//   Bound(slot, gain, emission)   the most its Light can give, for the culls
// Every stage works out the fully styled splat; the splat's style weight
// then blends that in over the plain one. Shape and Blend are one choice
// per style, read where they apply instead of hooked.
//
// The includer declares _MatrixWorldToObject (the splats' renderer) and
// _VecWorldSpaceCameraPos, and includes GaussianSplatting.hlsl first.
#ifndef GAUSSIAN_STYLE_HLSL
#define GAUSSIAN_STYLE_HLSL

#include "Style/GaussianStyleCore.hlsl"
#include "Style/GaussianStyleColor.hlsl"
#include "Style/GaussianStyleRim.hlsl"
#include "Style/GaussianStyleGlint.hlsl"
#include "Style/GaussianStyleFlow.hlsl"
#include "Style/GaussianStyleOcclusion.hlsl"
#include "Style/GaussianStyleDepth.hlsl"
#include "Style/GaussianStyleThinning.hlsl"
#include "Style/GaussianStyleQuantize.hlsl"
#include "Style/GaussianStyleShape.hlsl"
#include "Style/GaussianStyleBlend.hlsl"

// Moves a styled splat and scales its opacity by the share the style keeps
// of it. Runs before projection: the moved position feeds everything
// downstream, in both eyes alike. The hooks run in order, each from where
// the one before left the splat.
void ApplyStyleGeometry(uint idx, StyleSample style, inout SplatData splat, inout float3 worldPos)
{
    if (style.weight <= 0)
        return;
    StyleContext c = MakeStyleContext(style, idx, worldPos, splat.rot, splat.scale, 0);
    float3 moved = worldPos;
    float keep = 1.0;
    StyleThinningDisplace(c, moved, keep);
    StyleQuantizeDisplace(c, moved, keep);
    StyleRimDisplace(c, moved, keep);

    worldPos = lerp(worldPos, moved, style.weight);
    splat.pos = mul(_MatrixWorldToObject, float4(worldPos, 1)).xyz;
    splat.opacity *= (half)lerp(1.0, keep, style.weight);
}

// How far past white the style can drive a splat's colour (ApplyStyle), for
// the opacity culls to judge the splat by the light it may add
float StyleBrightnessBound(StyleSample style)
{
    float gain = 1.0;
    float emission = 0.0;
    StyleColorBound(style.slot, gain, emission);
    StyleRimBound(style.slot, gain, emission);
    StyleGlintBound(style.slot, gain, emission);
    return max(1.0, lerp(1.0, gain + emission, style.weight));
}

// How much of its light the style's masks leave a splat. 1 without a style
// or without a mask.
float StyleMask(StyleSample style, float3 centerWorldPos, uint idx)
{
    if (style.weight <= 0)
        return 1.0;
    StyleContext c = MakeStyleContext(style, idx, centerWorldPos, 0, 0, 0);
    float mask = 1.0;
    StyleOcclusionMask(c, mask);
    StyleDepthMask(c, mask);
    return lerp(1.0, mask, style.weight);
}

// The color the style gives a splat of this color (gamma space, after SH,
// animation tint and lighting), blended in by the splat's style weight,
// before its masks: for what takes the masks its own way
// (GaussianParticleEmitter fades its particles by them); everything else
// uses ApplyStyle
half3 ApplyStyleUnmasked(half3 color, StyleSample style, SplatData splat, float3 centerWorldPos, uint idx)
{
    if (style.weight <= 0)
        return color;
    StyleContext c = MakeStyleContext(style, idx, centerWorldPos, splat.rot, splat.scale, color);
    half3 base = color;
    StyleColorBase(c, base);
    float gain = 1.0;
    float3 emission = 0;
    StyleColorLight(c, gain, emission);
    StyleRimLight(c, gain, emission);
    StyleGlintLight(c, gain, emission);
    StyleFlowLight(c, gain, emission);
    half3 styled = base * (half)gain + (half3)emission;
    return lerp(color, styled, (half)style.weight);
}

// The styled color as drawn: the masks darken all the light the splat
// gives, base and emission alike
half3 ApplyStyle(half3 color, StyleSample style, SplatData splat, float3 centerWorldPos, uint idx)
{
    return ApplyStyleUnmasked(color, style, splat, centerWorldPos, idx) * (half)StyleMask(style, centerWorldPos, idx);
}

#endif // GAUSSIAN_STYLE_HLSL
