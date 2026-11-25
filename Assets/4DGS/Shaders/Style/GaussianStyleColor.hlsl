// SPDX-License-Identifier: MIT
// Color block (GaussianStyleColorBlock): the splat's base color, its own or
// one from the style's ramp, and its brightness.
#ifndef GAUSSIAN_STYLE_COLOR_HLSL
#define GAUSSIAN_STYLE_COLOR_HLSL

#include "GaussianStyleOcclusion.hlsl"
#include "GaussianStyleDepth.hlsl"

// GaussianSplatStyleColorSource
#define STYLE_COLOR_ORIGINAL 0
#define STYLE_COLOR_RAMP 1
// GaussianSplatStyleRampInput
#define STYLE_RAMP_LUMINANCE 0
#define STYLE_RAMP_DEPTH 1
#define STYLE_RAMP_WORLD_AXIS 2
#define STYLE_RAMP_VOLUME_DISTANCE 3
#define STYLE_RAMP_OCCLUSION 4
#define STYLE_RAMP_VOLUME_DEPTH 5

struct StyleColor
{
    bool on;
    uint source;            // STYLE_COLOR_*
    uint input;             // STYLE_RAMP_*
    float blend;            // how far the ramp color replaces the splat's own
    float rangeStart;       // ramp range start, for the world-space inputs
    float inverseRange;     // reciprocal of its length
    bool keepLuminance;     // the ramp color takes the splat's brightness
    float intensity;
    float3 axis;            // world direction of the World Axis input
    float intensityVariation; // share each splat's brightness strays, at random
};
#define STYLE_COLOR_ROWS 3
float4 _StyleColor[MAX_STYLES * STYLE_COLOR_ROWS];

// Mirrors GaussianStyleColorBlock.Data
StyleColor LoadStyleColor(uint slot)
{
    float4 r0 = _StyleColor[slot * STYLE_COLOR_ROWS];
    float4 r1 = _StyleColor[slot * STYLE_COLOR_ROWS + 1];
    float4 r2 = _StyleColor[slot * STYLE_COLOR_ROWS + 2];
    StyleColor b;
    b.on = r0.x > 0.5;
    b.source = (uint)r0.y;
    b.input = (uint)r0.z;
    b.blend = r0.w;
    b.rangeStart = r1.x;
    b.inverseRange = r1.y;
    b.keepLuminance = r1.z > 0.5;
    b.intensity = r1.w;
    b.axis = r2.xyz;
    b.intensityVariation = r2.w;
    return b;
}

// One ramp per row, a row per slot
Texture2D _StyleRampTex;
SamplerState sampler_StyleRampTex;
// Must match GaussianStyleColorBlock.RampResolution
#define STYLE_RAMP_RESOLUTION 256.0

void StyleColorBase(StyleContext c, inout half3 base)
{
    StyleColor b = LoadStyleColor(c.slot);
    if (!b.on || b.source != STYLE_COLOR_RAMP)
        return;

    float luminance = dot(c.color, half3(0.2126, 0.7152, 0.0722));
    // Luminance, a mask, or a world-space measure across the ramp range
    float t = luminance;
    if (b.input == STYLE_RAMP_OCCLUSION)
    {
        t = StyleOcclusionTerm(c.idx);
    }
    else if (b.input == STYLE_RAMP_VOLUME_DEPTH)
    {
        t = StyleDepthTerm(c);
    }
    else if (b.input != STYLE_RAMP_LUMINANCE)
    {
        float measure = b.input == STYLE_RAMP_DEPTH ? distance(c.worldPos, _VecWorldSpaceCameraPos.xyz)
                      : b.input == STYLE_RAMP_WORLD_AXIS ? dot(c.worldPos, b.axis)
                      : distance(c.worldPos, LoadStyleVolume(c.volume).center);
        t = (measure - b.rangeStart) * b.inverseRange;
    }
    // Texel centres, so both ends of the ramp are reached exactly, and the
    // slot's own row
    float u = (saturate(t) * (STYLE_RAMP_RESOLUTION - 1.0) + 0.5) / STYLE_RAMP_RESOLUTION;
    float v = (c.slot + 0.5) / MAX_STYLES;
    half3 ramp = (half3)_StyleRampTex.SampleLevel(sampler_StyleRampTex, float2(u, v), 0).rgb;
    if (b.keepLuminance)
        ramp *= (half)luminance;
    base = lerp(c.color, ramp, (half)b.blend);
}

void StyleColorLight(StyleContext c, inout float gain, inout float3 emission)
{
    StyleColor b = LoadStyleColor(c.slot);
    if (!b.on)
        return;
    // The splat's own lasting share, brighter or darker
    gain *= b.intensity * (1.0 + b.intensityVariation * (StyleHash01(c.idx ^ 0x51ed270bu) * 2.0 - 1.0));
}

void StyleColorBound(uint slot, inout float gain, inout float emission)
{
    StyleColor b = LoadStyleColor(slot);
    if (!b.on)
        return;
    gain *= b.intensity * (1.0 + b.intensityVariation);
}

#endif // GAUSSIAN_STYLE_COLOR_HLSL
