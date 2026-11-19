// SPDX-License-Identifier: MIT
// What every style block builds on: the style slots and volumes a
// GaussianStyleSet binds, which style a splat wears (StyleAt), and the
// context the blocks' hooks take. GaussianStyle.hlsl includes this, then the
// blocks.
#ifndef GAUSSIAN_STYLE_CORE_HLSL
#define GAUSSIAN_STYLE_CORE_HLSL

#include "../GaussianVolume.hlsl"

// Must match GaussianStyleSet.MaxStyles / MaxVolumes
#define MAX_STYLES 4
#define MAX_STYLE_VOLUMES 8

float _StyleWeight;         // GaussianStyler.weight; 0 = no style
float _StyleTime;

// A volume region and the slot of its style
struct StyleVolume
{
    float4x4 worldToLocal;
    float3 center;          // world position
    uint slot;
    float shape;            // GaussianVolumeShape
    float falloff;
    float weight;
    float scatter;          // the falloff as a per-splat scatter instead of a fade
};
#define STYLE_VOLUME_ROWS 2
uint _StyleVolumeCount;
float4x4 _StyleVolumeWorldToLocal[MAX_STYLE_VOLUMES];
float4 _StyleVolume[MAX_STYLE_VOLUMES * STYLE_VOLUME_ROWS];

// Mirrors GaussianStyleSet.AddVolume
StyleVolume LoadStyleVolume(uint index)
{
    float4 r0 = _StyleVolume[index * STYLE_VOLUME_ROWS];
    float4 r1 = _StyleVolume[index * STYLE_VOLUME_ROWS + 1];
    StyleVolume v;
    v.worldToLocal = _StyleVolumeWorldToLocal[index];
    v.center = r0.xyz;
    v.slot = (uint)r0.w;
    v.shape = r1.x;
    v.falloff = r1.y;
    v.weight = r1.z;
    v.scatter = r1.w;
    return v;
}

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

// What a block's hook knows about the splat it styles
struct StyleContext
{
    uint idx;           // splat index
    uint slot;          // the style's slot
    uint volume;        // the volume that gave the style
    float3 worldPos;    // the splat's centre
    float4 rot;
    float3 scale;
    half3 color;        // the splat's own color: gamma space, after SH, animation tint and lighting
};

StyleContext MakeStyleContext(StyleSample style, uint idx, float3 worldPos, float4 rot, float3 scale, half3 color)
{
    StyleContext c;
    c.idx = idx;
    c.slot = style.slot;
    c.volume = style.volume;
    c.worldPos = worldPos;
    c.rot = rot;
    c.scale = scale;
    c.color = color;
    return c;
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
        StyleVolume volume = LoadStyleVolume(i);
        float3 localPos = mul(volume.worldToLocal, float4(worldPos, 1)).xyz;
        float inside = GaussianVolumeWeight(localPos, volume.shape, volume.falloff);
        if (volume.scatter > 0)
            inside = lerp(inside, step(StyleHash01(idx ^ 0x7f4a7c15u), inside) * step(0.0001, inside), volume.scatter);
        float w = volume.weight * inside;
        if (w > style.weight)
        {
            style.weight = w;
            style.slot = volume.slot;
            style.volume = i;
        }
    }
    style.weight *= _StyleWeight;
    return style;
}

#endif // GAUSSIAN_STYLE_CORE_HLSL
