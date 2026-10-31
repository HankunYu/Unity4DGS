// SPDX-License-Identifier: MIT
// Style color (GaussianSplatStyle): which style a splat wears and how
// strongly (the strongest of its renderer's GaussianStyleVolumes, scaled by
// the GaussianStyler's weight), and the color that style gives it. Shared by
// the view data pass (SplatUtilities.compute) and GaussianParticleEmitter,
// which colors each particle the way its source splat is drawn.
// GaussianStyleSet binds the parameters, one slot per distinct style.
// View-dependent terms use the camera centre, so both eyes of a stereo pair
// agree, like the SH evaluation.
//
// The includer declares _MatrixWorldToObject (the splats' renderer) and
// _VecWorldSpaceCameraPos, and includes GaussianSplatting.hlsl first.
#ifndef GAUSSIAN_STYLE_HLSL
#define GAUSSIAN_STYLE_HLSL

#include "GaussianVolume.hlsl"

// Must match GaussianStyleSet.MaxStyles / MaxVolumes
#define MAX_STYLES 4
#define MAX_STYLE_VOLUMES 8

float _StyleWeight;         // GaussianStyler.weight; 0 = no style
float _StyleTime;
// Per slot: x GaussianSplatStyleColorSource, y GaussianSplatStyleRampInput,
// z depth ramp near, w its reciprocal range
float4 _StyleColorParams[MAX_STYLES];
// Per slot: x intensity, y rim amount, z rim power
float4 _StyleLightParams[MAX_STYLES];
// Per slot: x glint density, y rate, z intensity
float4 _StyleGlintParams[MAX_STYLES];
// One ramp per row, a row per slot
Texture2D _StyleRampTex;
SamplerState sampler_StyleRampTex;
// Must match GaussianSplatStyle.RampResolution
#define STYLE_RAMP_RESOLUTION 256.0

// Volume regions, each naming its style's slot. Params: x shape, y falloff,
// z weight, w slot.
uint _StyleVolumeCount;
float4x4 _StyleVolumeWorldToLocal[MAX_STYLE_VOLUMES];
float4 _StyleVolumeParams[MAX_STYLE_VOLUMES];

// The style a splat wears: its slot, and how far it replaces the plain look
struct StyleSample
{
    float weight;
    uint slot;
};

StyleSample NoStyle()
{
    StyleSample s;
    s.weight = 0;
    s.slot = 0;
    return s;
}

uint StyleHash(uint x)
{
    x ^= x >> 16; x *= 0x7feb352du;
    x ^= x >> 15; x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

float StyleHash01(uint x)
{
    return (StyleHash(x) >> 8) * (1.0 / 16777216.0);
}

// Brightness boost of a glinting splat: a fraction of splats flash briefly,
// each on its own clock so the flashes never line up
float StyleGlint(uint slot, uint idx)
{
    float4 g = _StyleGlintParams[slot];
    if (g.x <= 0 || StyleHash01(idx) >= g.x)
        return 0;
    float h = StyleHash01(idx ^ 0x68e31da4u);
    float phase = frac(_StyleTime * g.y * (0.5 + h) + h * 7.31);
    // A peak a sixth of the period wide, centred on half phase
    float flash = saturate(1.0 - abs(phase * 2.0 - 1.0) * 6.0);
    return flash * flash * g.z;
}

// How far past white the style can drive a splat's colour (ApplyStyle), for
// the opacity culls to judge the splat by the light it may add
float StyleBrightnessBound(StyleSample style)
{
    float glint = _StyleGlintParams[style.slot].x > 0 ? _StyleGlintParams[style.slot].z : 0;
    return max(1.0, lerp(1.0, _StyleLightParams[style.slot].x * (1.0 + glint), style.weight));
}

// Grazing-angle term. A flat splat's shortest axis is its surface normal; a
// round one has no meaningful normal, so the term fades with flatness.
float StyleRim(float rimPower, float4 rot, float3 scale, float3 centerWorldPos)
{
    float3 s = abs(scale);
    float minScale = min(s.x, min(s.y, s.z));
    float maxScale = max(s.x, max(s.y, s.z));
    float midScale = s.x + s.y + s.z - minScale - maxScale;
    float3 axis = s.x == minScale ? float3(1, 0, 0) : (s.y == minScale ? float3(0, 1, 0) : float3(0, 0, 1));
    float3 objNormal = QuatRotateVector(axis, rot);
    // Inverse transpose, as for the backface normal
    float3 worldNormal = normalize(mul(objNormal, (float3x3)_MatrixWorldToObject));
    float facing = abs(dot(worldNormal, normalize(_VecWorldSpaceCameraPos.xyz - centerWorldPos)));
    float flatness = 1.0 - minScale / max(midScale, 1e-8);
    return pow(saturate(1.0 - facing), rimPower) * flatness;
}

// The style of a splat at this position: its strongest volume's, scaled by
// the styler's weight
StyleSample StyleAt(float3 worldPos)
{
    StyleSample style = NoStyle();
    if (_StyleWeight <= 0)
        return style;
    for (uint i = 0; i < _StyleVolumeCount; i++)
    {
        float4 p = _StyleVolumeParams[i];
        float3 localPos = mul(_StyleVolumeWorldToLocal[i], float4(worldPos, 1)).xyz;
        float w = p.z * GaussianVolumeWeight(localPos, p.x, p.y);
        if (w > style.weight)
        {
            style.weight = w;
            style.slot = (uint)p.w;
        }
    }
    style.weight *= _StyleWeight;
    return style;
}

// The color the style gives a splat of this color (gamma space, after SH
// and animation tint), blended in by the splat's style weight
half3 ApplyStyle(half3 color, StyleSample style, SplatData splat, float3 centerWorldPos, uint idx)
{
    if (style.weight <= 0)
        return color;

    float4 colorParams = _StyleColorParams[style.slot];
    float4 light = _StyleLightParams[style.slot];
    half3 styled = color;
    if (colorParams.x > 0.5)
    {
        float t = colorParams.y < 0.5
            ? dot(color, half3(0.2126, 0.7152, 0.0722))
            : (distance(centerWorldPos, _VecWorldSpaceCameraPos.xyz) - colorParams.z) * colorParams.w;
        // Texel centres, so both ends of the ramp are reached exactly, and
        // the slot's own row
        float u = (saturate(t) * (STYLE_RAMP_RESOLUTION - 1.0) + 0.5) / STYLE_RAMP_RESOLUTION;
        float v = (style.slot + 0.5) / MAX_STYLES;
        styled = (half3)_StyleRampTex.SampleLevel(sampler_StyleRampTex, float2(u, v), 0).rgb;
    }
    float rim = light.y > 0 ? StyleRim(light.z, splat.rot, splat.scale, centerWorldPos) : 1.0;
    styled *= (half)(light.x * lerp(1.0, rim, light.y) * (1.0 + StyleGlint(style.slot, idx)));
    return lerp(color, styled, (half)style.weight);
}

#endif // GAUSSIAN_STYLE_HLSL
