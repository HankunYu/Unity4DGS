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
#pragma require 2darray
#pragma multi_compile _ GAUSSIAN_STEREO
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

#if defined(GAUSSIAN_STEREO)
Texture2DArray _StylizeSourceTex;
int _CustomStereoEyeIndex;
#else
Texture2D _StylizeSourceTex;
#endif

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
float _ColorMergeStrength;
float _ColorMergeLevels;
float _ColorMergeThreshold;
float _ColorMergeRadius;
float _ColorMergeEdgeProtect;
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
    #if defined(GAUSSIAN_STEREO)
    return _StylizeSourceTex.Load(int4(ClampPixelCoord(pixelPos), _CustomStereoEyeIndex, 0)).rgb;
    #else
    return _StylizeSourceTex.Load(int3(ClampPixelCoord(pixelPos), 0)).rgb;
    #endif
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

float3 QuantizeColor(float3 color, float levels)
{
    return floor(saturate(color) * levels + 0.5) / levels;
}

float3 ApplyColorMerge(float3 color, int2 pixelPos)
{
    float mergeStrength = saturate(_ColorMergeStrength);
    if (mergeStrength <= 0.0001)
        return color;

    float levels = max(_ColorMergeLevels, 2.0);
    float3 centerQuantized = QuantizeColor(color, levels);

    int radius = (int)round(lerp(1.0, 3.0, saturate(_ColorMergeRadius)));
    float radiusSigma = max((float)radius, 1.0) * 0.75;
    float colorSigma = lerp(0.03, 0.28, saturate(_ColorMergeThreshold));

    float3 accum = 0.0;
    float weightSum = 0.0;
    [unroll]
    for (int y = -3; y <= 3; ++y)
    {
        [unroll]
        for (int x = -3; x <= 3; ++x)
        {
            if (abs(x) > radius || abs(y) > radius)
                continue;

            float2 delta = float2(x, y);
            float spatialWeight = exp(-dot(delta, delta) / (2.0 * radiusSigma * radiusSigma));

            float3 sampleColor = SampleSource(pixelPos + int2(x, y));
            float3 sampleQuantized = QuantizeColor(sampleColor, levels);
            float3 colorDelta = sampleQuantized - centerQuantized;
            float colorDist2 = dot(colorDelta, colorDelta);
            float colorWeight = exp(-colorDist2 / (2.0 * colorSigma * colorSigma + 1e-5));

            float w = spatialWeight * colorWeight;
            accum += sampleQuantized * w;
            weightSum += w;
        }
    }

    float3 merged = weightSum > 1e-5 ? (accum / weightSum) : centerQuantized;

    float lumaL = dot(SampleSource(pixelPos + int2(-1, 0)), float3(0.2126, 0.7152, 0.0722));
    float lumaR = dot(SampleSource(pixelPos + int2(1, 0)), float3(0.2126, 0.7152, 0.0722));
    float lumaU = dot(SampleSource(pixelPos + int2(0, 1)), float3(0.2126, 0.7152, 0.0722));
    float lumaD = dot(SampleSource(pixelPos + int2(0, -1)), float3(0.2126, 0.7152, 0.0722));
    float edge = saturate(sqrt((lumaR - lumaL) * (lumaR - lumaL) + (lumaU - lumaD) * (lumaU - lumaD)) * 3.2);

    float protect = saturate(_ColorMergeEdgeProtect);
    float flatAreaMask = 1.0 - smoothstep(0.04, 0.22, edge);
    float edgeAttenuation = lerp(1.0, flatAreaMask, protect);
    float finalMergeStrength = mergeStrength * edgeAttenuation;
    return lerp(color, merged, finalMergeStrength);
}

half4 fragStylize(v2f i) : SV_Target
{
    int2 pixel = int2(i.vertex.xy);
    #if defined(GAUSSIAN_STEREO)
    float4 src = _StylizeSourceTex.Load(int4(pixel, _CustomStereoEyeIndex, 0));
    #else
    float4 src = _StylizeSourceTex.Load(int3(pixel, 0));
    #endif

    float3 styled = src.rgb;
    styled = ApplyVintage(styled);
    styled = ApplyBrushStroke(styled, pixel);
    styled = ApplyColorMerge(styled, pixel);
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
#pragma require 2darray
#pragma multi_compile _ GAUSSIAN_STEREO
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

#if defined(GAUSSIAN_STEREO)
Texture2DArray _StylizeSourceTex;
int _CustomStereoEyeIndex;
#else
Texture2D _StylizeSourceTex;
#endif

half4 fragCopy(v2f i) : SV_Target
{
    #if defined(GAUSSIAN_STEREO)
    return _StylizeSourceTex.Load(int4(i.vertex.xy, _CustomStereoEyeIndex, 0));
    #else
    return _StylizeSourceTex.Load(int3(i.vertex.xy, 0));
    #endif
}
ENDCG
        }
    }
}
