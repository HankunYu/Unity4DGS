// SPDX-License-Identifier: MIT
// Rim block (GaussianStyleRimBlock): a grazing-angle term, optionally broken
// up by a noise, that dims surfaces facing the camera, adds a Fresnel glow,
// and fades and spills the splats at silhouettes.
#ifndef GAUSSIAN_STYLE_RIM_HLSL
#define GAUSSIAN_STYLE_RIM_HLSL

#include "../GaussianNoise.hlsl"

struct StyleRim
{
    bool on;
    float power;            // sharpness of the rim
    float amount;           // how far brightness follows the rim
    float glow;             // light added at the rim
    float3 color;           // glow color
    float sourceTint;       // how far the glow takes the splat's own (lifted) color
    float noise;            // how far the noise breaks up the rim
    float noiseFrequency;   // per asset unit
    float noiseSpeed;       // patches per second
    float noiseGrain;
    float fade;             // how far the rim fades splats
    float spill;            // how far it pushes them out (world units)
};
#define STYLE_RIM_ROWS 4
float4 _StyleRim[MAX_STYLES * STYLE_RIM_ROWS];

// Mirrors GaussianStyleRimBlock.Data
StyleRim LoadStyleRim(uint slot)
{
    float4 r0 = _StyleRim[slot * STYLE_RIM_ROWS];
    float4 r1 = _StyleRim[slot * STYLE_RIM_ROWS + 1];
    float4 r2 = _StyleRim[slot * STYLE_RIM_ROWS + 2];
    float4 r3 = _StyleRim[slot * STYLE_RIM_ROWS + 3];
    StyleRim b;
    b.on = r0.x > 0.5;
    b.power = r0.y;
    b.amount = r0.z;
    b.glow = r0.w;
    b.color = r1.xyz;
    b.sourceTint = r1.w;
    b.noise = r2.x;
    b.noiseFrequency = r2.y;
    b.noiseSpeed = r2.z;
    b.noiseGrain = r2.w;
    b.fade = r3.x;
    b.spill = r3.y;
    return b;
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

// Grazing-angle term. A flat splat's shortest axis is its surface normal; a
// round one has no meaningful normal, so the term fades with flatness.
// View-dependent terms use the camera centre, so both eyes of a stereo pair
// agree, like the SH evaluation.
float StyleRimFacing(float rimPower, float4 rot, float3 scale, float3 centerWorldPos)
{
    float flatness;
    float3 worldNormal = StyleWorldNormal(rot, scale, flatness);
    float facing = abs(dot(worldNormal, normalize(_VecWorldSpaceCameraPos.xyz - centerWorldPos)));
    return pow(saturate(1.0 - facing), rimPower) * flatness;
}

// How much of the rim a splat keeps under the noise: patches of a drifting
// two-octave noise, cut to on and off, their edges broken per splat by the
// grain. 1 without noise. The noise is sampled at the splat's rest position
// (as stored in the asset), not where a mesh driver or animation moved it,
// so the patches stay on a deforming, walking body.
float StyleRimNoise(StyleRim b, uint idx)
{
    if (b.noise <= 0)
        return 1.0;
    float n = DriftingNoise(LoadSplatPos(idx) * b.noiseFrequency, _StyleTime * b.noiseSpeed);
    n += (StyleHash01(idx ^ 0x3c6ef372u) - 0.5) * b.noiseGrain;
    return lerp(1.0, smoothstep(0.35, 0.65, n), b.noise);
}

void StyleRimLight(StyleContext c, inout float gain, inout float3 emission)
{
    StyleRim b = LoadStyleRim(c.slot);
    if (!b.on)
        return;
    float rim = b.amount > 0 || b.glow > 0
        ? StyleRimFacing(b.power, c.rot, c.scale, c.worldPos) * StyleRimNoise(b, c.idx)
        : 1.0;
    gain *= lerp(1.0, rim, b.amount);
    // The Fresnel glow adds light at the edges on top of the dimmed base, in
    // the rim color tinted by the splat's own color, lifted to full
    // brightness so dark colors glow as strongly as light ones
    float3 glow = b.color * lerp(1.0, LiftColor(c.color, 1.0), b.sourceTint);
    emission += glow * (b.glow * rim);
}

// A glow taking the splat's color lifts it to white at most, so the rim
// color's brightest channel bounds it either way
void StyleRimBound(uint slot, inout float gain, inout float emission)
{
    StyleRim b = LoadStyleRim(slot);
    if (!b.on)
        return;
    emission += b.glow * max(b.color.r, max(b.color.g, b.color.b));
}

// Loosens the splat at its silhouette: the rim fades it and pushes it out
// along its surface normal, each splat by its own random share of the spill,
// squared so most move a little and a few far. From the camera centre, so
// both eyes agree.
void StyleRimDisplace(StyleContext c, inout float3 worldPos, inout float keep)
{
    StyleRim b = LoadStyleRim(c.slot);
    if (!b.on || (b.fade <= 0 && b.spill <= 0))
        return;

    float rim = saturate(StyleRimFacing(b.power, c.rot, c.scale, worldPos) * StyleRimNoise(b, c.idx));
    if (b.spill > 0)
    {
        // Mesh splats face out along their local +Z; inverse transpose, as
        // for the backface normal
        float3 normal = normalize(mul(QuatRotateVector(float3(0, 0, 1), c.rot), (float3x3)_MatrixWorldToObject));
        float share = StyleHash01(c.idx ^ 0x5be0cd19u);
        worldPos += normal * (b.spill * rim * share * share);
    }
    keep *= 1.0 - b.fade * rim;
}

#endif // GAUSSIAN_STYLE_RIM_HLSL
