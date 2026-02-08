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
float _PosterizeMix;
float _ShadowTintToGreen;
float _HighlightWarmth;
float _VignetteStrength;
float _BrushStrength;
float _BrushScale;
float _BrushAngleJitter;
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

float3 ApplyPosterizeDithered(float3 color, int2 pixelPos)
{
    float levels = max(_PosterizeLevels, 2.0);
    float invLevels = 1.0 / levels;
    float dither = Hash21(float2(pixelPos) * 0.71 + 43.17) - 0.5;
    float3 dithered = saturate(color + dither * invLevels * 0.9);
    return floor(dithered * levels + 1e-4) / levels;
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

int2 ClampPixelCoord(int2 pixelPos)
{
    int2 maxPixel = int2(_ScreenParams.xy) - 1;
    return clamp(pixelPos, int2(0, 0), maxPixel);
}

float3 SampleSource(int2 pixelPos)
{
    return _StylizeSourceTex.Load(int3(ClampPixelCoord(pixelPos), 0)).rgb;
}

float BrushScale01()
{
    return saturate((_BrushScale - 0.5) / 3.5);
}

float3 ApplyBrushStroke(float3 color, int2 pixelPos)
{
    float brushScale01 = BrushScale01();
    float2 p = float2(pixelPos);
    float3 left = SampleSource(pixelPos + int2(-1, 0));
    float3 right = SampleSource(pixelPos + int2(1, 0));
    float3 up = SampleSource(pixelPos + int2(0, 1));
    float3 down = SampleSource(pixelPos + int2(0, -1));

    float gx = dot(right - left, float3(0.2126, 0.7152, 0.0722));
    float gy = dot(up - down, float3(0.2126, 0.7152, 0.0722));
    float edge = saturate(sqrt(gx * gx + gy * gy) * 2.8 + 0.12);

    float tile = lerp(8.0, 22.0, brushScale01);
    float2 tileId = floor(p / tile);
    float noiseAngle = (Hash21(tileId + 31.1) - 0.5) * 6.2831853;
    float edgeAngle = atan2(gy, gx) + 1.5707963;
    float angleStep = 6.2831853 / 6.0;
    float quantizedEdgeAngle = round(edgeAngle / angleStep) * angleStep;
    float quantizeAmount = lerp(0.55, 0.2, brushScale01) * (1.0 - saturate(_BrushAngleJitter) * 0.5);
    float strokeAngle = lerp(edgeAngle, quantizedEdgeAngle, quantizeAmount);
    strokeAngle = lerp(strokeAngle, noiseAngle, saturate(_BrushAngleJitter) * 0.85);
    float2 dir = float2(cos(strokeAngle), sin(strokeAngle));

    float strokeLength = lerp(3.0, 11.5, brushScale01);
    int2 offA = int2(round(dir * strokeLength));
    int2 offB = int2(round(dir * (strokeLength * 0.5)));
    int2 offC = int2(round(dir * (strokeLength * 1.4)));

    float3 smear = color * 0.16;
    smear += SampleSource(pixelPos + offA) * 0.2;
    smear += SampleSource(pixelPos - offA) * 0.2;
    smear += SampleSource(pixelPos + offB) * 0.14;
    smear += SampleSource(pixelPos - offB) * 0.14;
    smear += SampleSource(pixelPos + offC) * 0.08;
    smear += SampleSource(pixelPos - offC) * 0.08;

    float2 local = frac(p / tile) - 0.5;
    float band = abs(frac(dot(local, dir) * 9.0) - 0.5) * 2.0;
    float brushFiber = (Hash21(tileId * 2.23 + floor(local * 18.0)) - 0.5) * 0.28;
    float stamp = 0.75 + 0.25 * step(0.46, Hash21(tileId * 3.11 + floor(local * 7.0)));

    float2 jitter = (float2(Hash21(tileId + 0.91), Hash21(tileId + 4.37)) - 0.5) * (tile * 0.35);
    int2 cellCenter = int2((tileId + 0.5) * tile + jitter);
    float3 cellColor = SampleSource(cellCenter);
    float cellMix = lerp(0.08, 0.15, edge) * saturate(_BrushStrength);
    smear = lerp(smear, cellColor, cellMix);
    smear *= 1.0 + brushFiber;

    float edgeMask = smoothstep(0.05, 0.32, edge);
    float strokeMask = saturate(edgeMask * (0.5 + edge * 0.7) - band * 0.16);
    strokeMask *= stamp;
    float brushBlend = saturate(_BrushStrength) * (0.2 + 0.8 * strokeMask);
    brushBlend = saturate(brushBlend + edge * 0.06);

    float lineMask = smoothstep(0.25, 0.9, edge) * lerp(0.7, 1.0, stamp);
    float3 inked = smear * (1.0 - 0.2 * lineMask);

    return lerp(color, inked, brushBlend);
}

half4 fragStylize(v2f i) : SV_Target
{
    int2 pixel = int2(i.vertex.xy);
    float4 src = _StylizeSourceTex.Load(int3(pixel, 0));

    float3 styled = src.rgb;
    styled = ApplyVintage(styled);
    styled = ApplyBrushStroke(styled, pixel);
    float3 posterized = ApplyPosterizeDithered(styled, pixel);
    styled = lerp(styled, posterized, saturate(_PosterizeMix));
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
