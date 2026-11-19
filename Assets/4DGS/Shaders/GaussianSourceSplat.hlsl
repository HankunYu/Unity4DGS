// A source renderer's splats as it draws them, for compute shaders that
// derive splats of their own from another renderer: GaussianParticleEmitter
// (particles born on the source) and GaussianGridLinks (lines to the
// source). The source binds its data with
// GaussianSplatRenderer.SetAssetDataOnCS, SetStyleOnCS and
// SetLightingOnCS; the includer sets _SourceObjectToWorld,
// _MatrixWorldToObject (its inverse) and the camera.
//
// The includer includes GaussianSplatting.hlsl first.
#ifndef GAUSSIAN_SOURCE_SPLAT_HLSL
#define GAUSSIAN_SOURCE_SPLAT_HLSL

// The source's transform and the camera its style is seen from (rim and
// depth ramp), for GaussianStyle.hlsl
float4x4 _MatrixWorldToObject;
float4 _VecWorldSpaceCameraPos;
// The source's style and scene lighting, bound by
// GaussianSplatRenderer.SetStyleOnCS and SetLightingOnCS
#include "GaussianStyle.hlsl"
#include "GaussianLighting.hlsl"

// The source, bound by GaussianSplatRenderer.SetAssetDataOnCS
uint _SplatCount;
uint _SrcSplatCount;
StructuredBuffer<float4> _AnimOutputData;
uint _AnimDataValid;
StructuredBuffer<float4> _DeformedBase;
uint _DeformedBaseValid;
StructuredBuffer<float4> _MorphedData;
uint _MorphDataValid;
float4x4 _SourceObjectToWorld;

// The source splat as the view data pass sees it, in world space, its color
// (gamma) lit and styled as drawn but without view-dependent SH, and with
// the style's masks apart: mask is how much of that color they leave
void LoadSourceSplat(uint idx, out float3 worldPos, out float3 color, out float opacity, out float mask)
{
    SplatData splat = LoadSplatData(idx, 0);
    color = splat.sh.col;
    opacity = splat.opacity;
    if (_MorphDataValid != 0)
    {
        float4 md0 = _MorphedData[idx * 4];
        splat.pos = md0.xyz;
        splat.rot = _MorphedData[idx * 4 + 1];
        splat.scale = _MorphedData[idx * 4 + 2].xyz;
        opacity = md0.w;
        color = _MorphedData[idx * 4 + 3].rgb;
    }
    else if (_DeformedBaseValid != 0)
    {
        splat.pos = _DeformedBase[idx * 2].xyz;
        splat.rot = _DeformedBase[idx * 2 + 1];
    }
    if (_AnimDataValid != 0 && idx < _SrcSplatCount)
    {
        float4 posOpacity = _AnimOutputData[idx * 3];
        float4 scaleBlend = _AnimOutputData[idx * 3 + 1];
        float4 tint = _AnimOutputData[idx * 3 + 2];
        splat.pos += posOpacity.xyz;
        opacity *= posOpacity.w;
        color = lerp(color, tint.rgb, scaleBlend.y);
    }
    worldPos = mul(_SourceObjectToWorld, float4(splat.pos, 1)).xyz;
    color = LightSplat((half3)color, splat.rot, splat.scale, worldPos);
    StyleSample style = StyleAt(worldPos, idx);
    color = ApplyStyleUnmasked((half3)color, style, splat, worldPos, idx);
    mask = StyleMask(style, worldPos, idx);
}

// The same with the masks in the color, as the splat is drawn
void LoadSourceSplat(uint idx, out float3 worldPos, out float3 color, out float opacity)
{
    float mask;
    LoadSourceSplat(idx, worldPos, color, opacity, mask);
    color *= mask;
}

// Just the world position of LoadSourceSplat
float3 LoadSourcePos(uint idx)
{
    float3 pos;
    if (_MorphDataValid != 0)
        pos = _MorphedData[idx * 4].xyz;
    else if (_DeformedBaseValid != 0)
        pos = _DeformedBase[idx * 2].xyz;
    else
        pos = LoadSplatPos(idx);
    if (_AnimDataValid != 0 && idx < _SrcSplatCount)
        pos += _AnimOutputData[idx * 3].xyz;
    return mul(_SourceObjectToWorld, float4(pos, 1)).xyz;
}

// The splat's local +Z in world space, the surface normal for mesh splats
float3 LoadSourceNormal(uint idx)
{
    float4 rot;
    if (_MorphDataValid != 0)
        rot = _MorphedData[idx * 4 + 1];
    else if (_DeformedBaseValid != 0)
        rot = _DeformedBase[idx * 2 + 1];
    else
        rot = LoadSplatData(idx, 0).rot;
    float3 normal = mul((float3x3)_SourceObjectToWorld, QuatRotateVector(float3(0, 0, 1), rot));
    return dot(normal, normal) > 1e-12 ? normalize(normal) : float3(0, 1, 0);
}

#endif // GAUSSIAN_SOURCE_SPLAT_HLSL
