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
#include "GaussianNoise.hlsl"

// Must match GaussianStyleSet.MaxStyles / MaxVolumes
#define MAX_STYLES 4
#define MAX_STYLE_VOLUMES 8

float _StyleWeight;         // GaussianStyler.weight; 0 = no style
float _StyleTime;
// Per slot: x GaussianSplatStyleColorSource, y GaussianSplatStyleRampInput,
// z ramp range start, w its reciprocal length
float4 _StyleColorParams[MAX_STYLES];
// Per slot: xyz the world direction of the World Axis ramp input
float4 _StyleRampAxes[MAX_STYLES];
// Per slot: x how far the ramp color replaces the splat's own, y 1 when the
// ramp color takes the splat's brightness
float4 _StyleRampBlend[MAX_STYLES];
// Per slot: x intensity, y rim amount, z rim power, w rim glow
float4 _StyleLightParams[MAX_STYLES];
// Per slot: rgb rim glow color, w how far the glow takes the splat's own
// (lifted) color
float4 _StyleRimColors[MAX_STYLES];
// Per slot: x how far the noise breaks up the rim, y noise frequency (per
// asset unit), z noise speed (patches per second), w grain
float4 _StyleRimNoise[MAX_STYLES];
// Per slot: x glint density, y rate, z intensity
float4 _StyleGlintParams[MAX_STYLES];
// Per slot: x how far brightness follows the flow waves, y their speed (world
// units per second), z distance between waves, w tail length (world units)
float4 _StyleFlowParams[MAX_STYLES];
// Per slot: xyz flow direction (world, normalized), w columns per world unit
float4 _StyleFlowAxes[MAX_STYLES];
// Per slot: x step the waves advance in (world units, 0 = smooth)
float4 _StyleFlowSteps[MAX_STYLES];
// Per slot: x how far the baked occlusion darkens, y its contrast
float4 _StyleOcclusionParams[MAX_STYLES];
// Per slot: x how far the depth mask darkens, y the reciprocal of its range,
// z its offset away from the camera, w GaussianSplatStyleDepthReference
float4 _StyleDepthParams[MAX_STYLES];
// Baked occlusion of the splats (GaussianMeshBinding): a byte per splat, 0
// covered to 255 open, four to a uint. The splats' renderer binds it.
StructuredBuffer<uint> _SplatOcclusion;
uint _SplatOcclusionValid;
// The mesh each splat sits on (GaussianMeshDriver): its index, 16 bits per
// splat, two to a uint with the first in the low half; and each mesh's
// centre as posed, in the splats' object space
StructuredBuffer<uint> _SplatMesh;
StructuredBuffer<float4> _SplatMeshCenters;
uint _SplatMeshValid;
float4x4 _SplatMeshObjectToWorld;
// One ramp per row, a row per slot
Texture2D _StyleRampTex;
SamplerState sampler_StyleRampTex;
// Must match GaussianSplatStyle.RampResolution
#define STYLE_RAMP_RESOLUTION 256.0

// Volume regions, each naming its style's slot. Params: x shape, y falloff,
// z weight, w slot. Edge: x scatter (the falloff as a per-splat scatter
// instead of a fade). Centers: xyz the volume's world position.
uint _StyleVolumeCount;
float4x4 _StyleVolumeWorldToLocal[MAX_STYLE_VOLUMES];
float4 _StyleVolumeParams[MAX_STYLE_VOLUMES];
float4 _StyleVolumeEdge[MAX_STYLE_VOLUMES];
float4 _StyleVolumeCenters[MAX_STYLE_VOLUMES];

// The style a splat wears: its slot, how far it replaces the plain look, and
// the volume that gave it
struct StyleSample
{
    float weight;
    uint slot;
    uint volume;
};

StyleSample NoStyle()
{
    StyleSample s;
    s.weight = 0;
    s.slot = 0;
    s.volume = 0;
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

// Brightness of a splat under the flow: waves run along the flow direction,
// each lit at its front and fading over its tail. Space is cut into columns
// across the direction, each with its own offset and speed, so neighbouring
// columns never run in step. Rounded, so points snapped to a grid of the
// column size sit at column centres.
float StyleFlow(uint slot, float3 worldPos)
{
    float4 f = _StyleFlowParams[slot];
    if (f.x <= 0)
        return 1.0;
    float4 axis = _StyleFlowAxes[slot];
    float3 side = normalize(cross(axis.xyz, abs(axis.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
    float3 up = cross(axis.xyz, side);
    int2 column = (int2)round(float2(dot(worldPos, side), dot(worldPos, up)) * axis.w);
    uint seed = (uint)column.x * 0x9e3779b1u ^ (uint)column.y * 0x85ebca6bu ^ slot;
    float h = StyleHash01(seed);
    float travelled = _StyleTime * f.y * (0.6 + 0.8 * StyleHash01(seed ^ 0x2c1b3c6du)) + h * f.z;
    float along = dot(worldPos, axis.xyz);
    float stride = _StyleFlowSteps[slot].x;
    if (stride > 0)
    {
        travelled = floor(travelled / stride) * stride;
        along = round(along / stride) * stride;
    }
    // Distance the nearest wave front has run past this point
    float behind = frac((travelled - along) / f.z) * f.z;
    float wave = saturate(1.0 - behind / f.w);
    return lerp(1.0, wave * wave, f.x);
}

// How open a splat's surroundings are, from its baked occlusion: 0 covered,
// 1 open, and 1 for splats without one
float StyleOcclusion(uint idx)
{
    if (_SplatOcclusionValid == 0)
        return 1.0;
    uint word = _SplatOcclusion[idx >> 2];
    return ((word >> ((idx & 3) * 8)) & 255) * (1.0 / 255.0);
}

// World position a splat's depth is measured against
// (GaussianSplatStyleDepthReference): its style volume's centre, or the
// centre of its own mesh where a mesh driver gives one
float3 StyleDepthReference(StyleSample style, uint idx)
{
    if (_StyleDepthParams[style.slot].w > 0.5 && _SplatMeshValid != 0)
    {
        uint mesh = (_SplatMesh[idx >> 1] >> ((idx & 1) * 16)) & 0xFFFFu;
        return mul(_SplatMeshObjectToWorld, float4(_SplatMeshCenters[mesh].xyz, 1)).xyz;
    }
    return _StyleVolumeCenters[style.volume].xyz;
}

// The style's depth mask: 1 nearer the camera than its depth reference by
// half the range or more, 0 that far behind it. Distances from the camera
// centre, so it does not change as the view turns.
float StyleDepth(StyleSample style, float3 centerWorldPos, uint idx)
{
    float4 p = _StyleDepthParams[style.slot];
    float3 cam = _VecWorldSpaceCameraPos.xyz;
    float behind = distance(centerWorldPos, cam) - distance(StyleDepthReference(style, idx), cam) - p.z;
    return saturate(0.5 - behind * p.y);
}

// Raises a color's brightest channel towards 1 by the lift, scaling all
// channels alike so hue and saturation stay; near black the gain is capped,
// since hue there is mostly noise
float3 LiftColor(float3 color, float lift)
{
    static const float MaxGain = 16.0;
    float value = max(max(color.r, color.g), color.b);
    float target = lerp(value, max(value, 1.0), lift);
    return color * min(target / max(value, 1e-4), MaxGain);
}

// How far past white the style can drive a splat's colour (ApplyStyle), for
// the opacity culls to judge the splat by the light it may add. A glow
// taking the splat's color lifts it to white at most, so the rim color's
// brightest channel bounds it either way.
float StyleBrightnessBound(StyleSample style)
{
    float glint = _StyleGlintParams[style.slot].x > 0 ? _StyleGlintParams[style.slot].z : 0;
    float4 light = _StyleLightParams[style.slot];
    float3 rimColor = _StyleRimColors[style.slot].rgb;
    float rimGlow = light.w * max(rimColor.r, max(rimColor.g, rimColor.b));
    return max(1.0, lerp(1.0, light.x * (1.0 + glint) + rimGlow, style.weight));
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

// How much of the rim a splat keeps under its style's rim noise: patches of
// a drifting two-octave noise, cut to on and off, their edges broken per
// splat by the grain. 1 without noise. The noise is sampled at the splat's
// rest position (as stored in the asset), not where a mesh driver or
// animation moved it, so the patches stay on a deforming, walking body.
float StyleRimNoise(uint slot, uint idx)
{
    float4 p = _StyleRimNoise[slot];
    if (p.x <= 0)
        return 1.0;
    float n = DriftingNoise(LoadSplatPos(idx) * p.y, _StyleTime * p.z);
    n += (StyleHash01(idx ^ 0x3c6ef372u) - 0.5) * p.w;
    return lerp(1.0, smoothstep(0.35, 0.65, n), p.x);
}

// The style of a splat at this position: its strongest volume's, scaled by
// the styler's weight. A scattering volume takes each splat in its falloff
// whole or not at all, the nearer its inside the more of them, so a moving
// volume changes the splats over one by one instead of along a line
StyleSample StyleAt(float3 worldPos, uint idx)
{
    StyleSample style = NoStyle();
    if (_StyleWeight <= 0)
        return style;
    for (uint i = 0; i < _StyleVolumeCount; i++)
    {
        float4 p = _StyleVolumeParams[i];
        float3 localPos = mul(_StyleVolumeWorldToLocal[i], float4(worldPos, 1)).xyz;
        float inside = GaussianVolumeWeight(localPos, p.x, p.y);
        float scatter = _StyleVolumeEdge[i].x;
        if (scatter > 0)
            inside = lerp(inside, step(StyleHash01(idx ^ 0x7f4a7c15u), inside) * step(0.0001, inside), scatter);
        float w = p.z * inside;
        if (w > style.weight)
        {
            style.weight = w;
            style.slot = (uint)p.w;
            style.volume = i;
        }
    }
    style.weight *= _StyleWeight;
    return style;
}

// How much of its light the style's masks leave a splat: covered creases
// (occlusion) and the side facing away from the camera (depth) go dark. 1
// without a style or with both masks off.
float StyleMask(StyleSample style, float3 centerWorldPos, uint idx)
{
    if (style.weight <= 0)
        return 1.0;
    float2 occlusion = _StyleOcclusionParams[style.slot].xy;
    float mask = lerp(1.0, pow(StyleOcclusion(idx), occlusion.y), occlusion.x)
               * lerp(1.0, StyleDepth(style, centerWorldPos, idx), _StyleDepthParams[style.slot].x);
    return lerp(1.0, mask, style.weight);
}

// The color the style gives a splat of this color (gamma space, after SH
// and animation tint), blended in by the splat's style weight, before its
// masks: for what takes the masks its own way (GaussianParticleEmitter fades
// its particles by them); everything else uses ApplyStyle
half3 ApplyStyleUnmasked(half3 color, StyleSample style, SplatData splat, float3 centerWorldPos, uint idx)
{
    if (style.weight <= 0)
        return color;

    float4 colorParams = _StyleColorParams[style.slot];
    float4 light = _StyleLightParams[style.slot];
    half3 styled = color;
    if (colorParams.x > 0.5)
    {
        float luminance = dot(color, half3(0.2126, 0.7152, 0.0722));
        // GaussianSplatStyleRampInput: luminance, a mask, or a world-space
        // measure across the ramp range
        float t = luminance;
        if (colorParams.y > 3.5)
        {
            t = colorParams.y < 4.5 ? StyleOcclusion(idx) : StyleDepth(style, centerWorldPos, idx);
        }
        else if (colorParams.y > 0.5)
        {
            float measure = colorParams.y < 1.5 ? distance(centerWorldPos, _VecWorldSpaceCameraPos.xyz)
                          : colorParams.y < 2.5 ? dot(centerWorldPos, _StyleRampAxes[style.slot].xyz)
                          : distance(centerWorldPos, _StyleVolumeCenters[style.volume].xyz);
            t = (measure - colorParams.z) * colorParams.w;
        }
        // Texel centres, so both ends of the ramp are reached exactly, and
        // the slot's own row
        float u = (saturate(t) * (STYLE_RAMP_RESOLUTION - 1.0) + 0.5) / STYLE_RAMP_RESOLUTION;
        float v = (style.slot + 0.5) / MAX_STYLES;
        half3 ramp = (half3)_StyleRampTex.SampleLevel(sampler_StyleRampTex, float2(u, v), 0).rgb;
        float2 blend = _StyleRampBlend[style.slot].xy;
        if (blend.y > 0.5)
            ramp *= (half)luminance;
        styled = lerp(color, ramp, (half)blend.x);
    }
    float rim = light.y > 0 || light.w > 0
        ? StyleRim(light.z, splat.rot, splat.scale, centerWorldPos) * StyleRimNoise(style.slot, idx)
        : 1.0;
    styled *= (half)(light.x * lerp(1.0, rim, light.y) * (1.0 + StyleGlint(style.slot, idx))
                    * StyleFlow(style.slot, centerWorldPos));
    // The Fresnel glow adds light at the edges on top of the dimmed base, in
    // the rim color tinted by the splat's own color, lifted to full
    // brightness so dark colors glow as strongly as light ones
    float4 rimColor = _StyleRimColors[style.slot];
    float3 glow = rimColor.rgb * lerp(1.0, LiftColor(color, 1.0), rimColor.w);
    styled += (half3)(glow * (light.w * rim));
    return lerp(color, styled, (half)style.weight);
}

// The styled color as drawn: the masks darken all the light the splat
// gives, base and glow alike
half3 ApplyStyle(half3 color, StyleSample style, SplatData splat, float3 centerWorldPos, uint idx)
{
    return ApplyStyleUnmasked(color, style, splat, centerWorldPos, idx) * (half)StyleMask(style, centerWorldPos, idx);
}

#endif // GAUSSIAN_STYLE_HLSL
