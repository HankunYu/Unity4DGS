// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Stylize"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One Zero

CGPROGRAM
#pragma vertex vert
#pragma fragment fragStylize
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert(uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 4.0 - 1.0;
    o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _StylizeSourceTex;

float _GrainIntensity;
float _GrainScale;
float _GrainTemporalJitter;
float _VintageStrength;
float _PosterizeLevels;
float _ShadowTintToGreen;
float _HighlightWarmth;
float _VignetteStrength;
float _EffectBlend;

float Hash21(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 78.233);
    return frac(p.x * p.y);
}

float3 ApplyVintage(float3 color)
{
    float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
    float shadowMask = saturate(1.0 - luma * 1.5);
    float highlightMask = saturate((luma - 0.45) * 2.0);

    float3 shadowTint = float3(0.0, _ShadowTintToGreen, 0.0) * shadowMask;
    float3 highlightTint = float3(_HighlightWarmth, _HighlightWarmth * 0.45, 0.0) * highlightMask;

    float3 graded = color + lerp(shadowTint, highlightTint, 0.5);
    float contrast = lerp(1.0, 0.85, saturate(_VintageStrength));
    graded = (graded - 0.5) * contrast + 0.5;

    return lerp(color, graded, saturate(_VintageStrength));
}

float3 ApplyPosterize(float3 color)
{
    float levels = max(_PosterizeLevels, 2.0);
    return floor(saturate(color) * levels + 1e-4) / levels;
}

float3 ApplyGrain(float3 color, float2 pixelPos)
{
    float temporalSpeed = lerp(0.35, 3.0, saturate(_GrainTemporalJitter));
    float t = _Time.y * temporalSpeed;
    float2 noisePos = pixelPos * max(_GrainScale, 0.1) + t;
    float grain = Hash21(noisePos) - 0.5;
    return color + grain * (_GrainIntensity * 0.22);
}

float3 ApplyVignette(float3 color, float2 pixelPos)
{
    float2 uv = (pixelPos + 0.5) / max(_ScreenParams.xy, 1.0);
    float2 centered = uv * 2.0 - 1.0;
    float dist2 = dot(centered, centered);
    float vig = 1.0 - _VignetteStrength * smoothstep(0.2, 1.1, dist2);
    return color * vig;
}

half4 fragStylize(v2f i) : SV_Target
{
    int2 pixel = int2(i.vertex.xy);
    float4 src = _StylizeSourceTex.Load(int3(pixel, 0));

    float3 styled = src.rgb;
    styled = ApplyVintage(styled);
    styled = ApplyPosterize(styled);
    styled = ApplyGrain(styled, i.vertex.xy);
    styled = ApplyVignette(styled, i.vertex.xy);

    styled = lerp(src.rgb, saturate(styled), saturate(_EffectBlend));
    return float4(styled, src.a);
}
ENDCG
        }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One Zero

CGPROGRAM
#pragma vertex vert
#pragma fragment fragCopy
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert(uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 4.0 - 1.0;
    o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _StylizeSourceTex;

half4 fragCopy(v2f i) : SV_Target
{
    return _StylizeSourceTex.Load(int3(i.vertex.xy, 0));
}
ENDCG
        }
    }
}
