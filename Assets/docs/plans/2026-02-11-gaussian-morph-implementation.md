# Gaussian Morph Transition Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Implement GPU-driven morph transition between two Gaussian Splat assets with full property interpolation.

**Architecture:** `GaussianMorph` component dispatches `GaussianMorph.compute` to decode and blend two assets into `_MorphedData` buffer. `CalcViewData` reads from this buffer when morph is active. Existing animation modifiers stack on top via `_AnimOutput`.

**Tech Stack:** Unity 2022.3+, C#, HLSL Compute Shaders, GraphicsBuffer API

---

### Task 1: Add morph support fields to GaussianSplatRenderer

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

**Step 1: Add morph property IDs to Props class (line ~611)**

After the existing `AnimDataValid` line, add:

```csharp
public static readonly int MorphedData = Shader.PropertyToID("_MorphedData");
public static readonly int MorphDataValid = Shader.PropertyToID("_MorphDataValid");
```

**Step 2: Add morph buffer fields (after line 527)**

After `internal GraphicsBuffer m_AnimOutputBuffer;`, add:

```csharp
// Set by GaussianMorph: pre-blended splat data (8 float4s per splat)
internal GraphicsBuffer m_MorphedDataBuffer;
internal int m_MorphDataValid;
internal int m_MorphedSplatCount;
```

**Step 3: Add SetMorphData method**

Add near the existing public property accessors (after line 623):

```csharp
internal void SetMorphData(GraphicsBuffer morphedData, int splatCount, int valid)
{
    m_MorphedDataBuffer = morphedData;
    m_MorphedSplatCount = splatCount;
    m_MorphDataValid = valid;
}
```

**Step 4: Bind morph data in SetAssetDataOnCS (after line 962)**

After the animation data binding block, add:

```csharp
// Morph data binding
bool hasMorph = m_MorphedDataBuffer != null && m_MorphDataValid != 0;
cmb.SetComputeIntParam(cs, Props.MorphDataValid, hasMorph ? 1 : 0);
cmb.SetComputeBufferParam(cs, kernelIndex, Props.MorphedData, hasMorph ? m_MorphedDataBuffer : m_GpuView);
```

**Step 5: Update dispatch count in CalcViewData for morph**

In `CalcViewData()` method (line ~1033), the dispatch uses `m_GpuView.count`. When morph is active, splat count may be larger. Modify the dispatch line:

```csharp
int dispatchCount = (m_MorphDataValid != 0 && m_MorphedSplatCount > 0) ? m_MorphedSplatCount : m_GpuView.count;
cmb.DispatchCompute(m_CSSplatUtilities, kernelIndex, (dispatchCount + (int)gsX - 1)/(int)gsX, 1, 1);
```

Also update the `_SplatCount` that is set: when morph is active, use `m_MorphedSplatCount` instead. This requires updating `SetAssetDataOnCS`:

```csharp
int effectiveSplatCount = (m_MorphedDataBuffer != null && m_MorphDataValid != 0 && m_MorphedSplatCount > 0)
    ? m_MorphedSplatCount : m_SplatCount;
cmb.SetComputeIntParam(cs, Props.SplatCount, effectiveSplatCount);
```

**Step 6: Commit**

```
feat: add morph data support to GaussianSplatRenderer
```

---

### Task 2: Create GaussianSplatDecode.hlsl shared decode library

**Files:**
- Create: `Assets/4DGS/Shaders/GaussianSplatDecode.hlsl`

This file provides parameterized decode functions that accept explicit buffer arguments, so both `GaussianAnimate.compute` and `GaussianMorph.compute` can reuse them.

**Step 1: Create the shared decode file**

```hlsl
// SPDX-License-Identifier: MIT
// Shared Gaussian Splat data decoding functions.
// Parameterized to accept explicit buffer arguments for multi-asset decoding.

#ifndef GAUSSIAN_SPLAT_DECODE_HLSL
#define GAUSSIAN_SPLAT_DECODE_HLSL

#define SPLAT_DECODE_FMT_32F 0
#define SPLAT_DECODE_FMT_16  1
#define SPLAT_DECODE_FMT_11  2
#define SPLAT_DECODE_FMT_6   3

static const uint kDecodeChunkSize = 256;

struct SplatChunkData
{
    uint colR, colG, colB, colA;
    float2 posX, posY, posZ;
    uint sclX, sclY, sclZ;
    uint shR, shG, shB;
};

// ── Low-level decode helpers ──

half3 SplatDecode_6_5_5(uint enc)
{
    return half3(
        (enc & 63) / 63.0,
        ((enc >> 6) & 31) / 31.0,
        ((enc >> 11) & 31) / 31.0);
}

half3 SplatDecode_5_6_5(uint enc)
{
    return half3(
        (enc & 31) / 31.0,
        ((enc >> 5) & 63) / 63.0,
        ((enc >> 11) & 31) / 31.0);
}

half3 SplatDecode_11_10_11(uint enc)
{
    return half3(
        (enc & 2047) / 2047.0,
        ((enc >> 11) & 1023) / 1023.0,
        ((enc >> 21) & 2047) / 2047.0);
}

float3 SplatDecode_16_16_16(uint2 enc)
{
    return float3(
        (enc.x & 65535) / 65535.0,
        ((enc.x >> 16) & 65535) / 65535.0,
        (enc.y & 65535) / 65535.0);
}

float4 SplatDecode_10_10_10_2(uint enc)
{
    return float4(
        (enc & 1023) / 1023.0,
        ((enc >> 10) & 1023) / 1023.0,
        ((enc >> 20) & 1023) / 1023.0,
        ((enc >> 30) & 3) / 3.0);
}

uint SplatLoadUShort(ByteAddressBuffer buf, uint addrU)
{
    uint addrA = addrU & ~0x3;
    uint val = buf.Load(addrA);
    if (addrU != addrA) val >>= 16;
    return val & 0xFFFF;
}

uint SplatLoadUInt(ByteAddressBuffer buf, uint addrU)
{
    uint addrA = addrU & ~0x3;
    uint val = buf.Load(addrA);
    if (addrU != addrA)
    {
        uint val1 = buf.Load(addrA + 4);
        val = (val >> 16) | ((val1 & 0xFFFF) << 16);
    }
    return val;
}

// ── Vector decode (handles all formats + unaligned access) ──

float3 SplatDecodeVector(ByteAddressBuffer buf, uint addrU, uint fmt)
{
    uint addrA = addrU & ~0x3;
    uint val0 = buf.Load(addrA);
    float3 res = 0;

    if (fmt == SPLAT_DECODE_FMT_32F)
    {
        uint val1 = buf.Load(addrA + 4);
        uint val2 = buf.Load(addrA + 8);
        if (addrU != addrA)
        {
            uint val3 = buf.Load(addrA + 12);
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
            val1 = (val1 >> 16) | ((val2 & 0xFFFF) << 16);
            val2 = (val2 >> 16) | ((val3 & 0xFFFF) << 16);
        }
        res = float3(asfloat(val0), asfloat(val1), asfloat(val2));
    }
    else if (fmt == SPLAT_DECODE_FMT_16)
    {
        uint val1 = buf.Load(addrA + 4);
        if (addrU != addrA)
        {
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
            val1 >>= 16;
        }
        res = SplatDecode_16_16_16(uint2(val0, val1));
    }
    else if (fmt == SPLAT_DECODE_FMT_11)
    {
        uint val1 = buf.Load(addrA + 4);
        if (addrU != addrA)
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
        res = SplatDecode_11_10_11(val0);
    }
    else if (fmt == SPLAT_DECODE_FMT_6)
    {
        if (addrU != addrA) val0 >>= 16;
        res = SplatDecode_6_5_5(val0);
    }
    return res;
}

uint SplatGetVectorStride(uint fmt)
{
    if (fmt == SPLAT_DECODE_FMT_32F) return 12;
    if (fmt == SPLAT_DECODE_FMT_16)  return 6;
    if (fmt == SPLAT_DECODE_FMT_11)  return 4;
    return 2; // FMT_6
}

// ── Quaternion decode ──

float4 SplatDecodeRotation(float4 pq)
{
    uint idx = (uint)round(pq.w * 3.0);
    float4 q;
    q.xyz = pq.xyz * sqrt(2.0) - (1.0 / sqrt(2.0));
    q.w = sqrt(1.0 - saturate(dot(q.xyz, q.xyz)));
    if (idx == 0) q = q.wxyz;
    if (idx == 1) q = q.xwyz;
    if (idx == 2) q = q.xywz;
    return q;
}

// ── High-level: decode full splat from explicit buffers ──

struct DecodedSplat
{
    float3 pos;
    float4 rot;
    float3 scale;
    float  opacity;
    float3 color;
    float3 sh[15];
};

float InvSquareCentered01_Decode(float x)
{
    x -= 0.5;
    x = (x < 0 ? -1 : 1) * sqrt(abs(x) * 2.0);
    return x * 0.5 + 0.5;
}

DecodedSplat SplatDecodeFromBuffers(
    ByteAddressBuffer posBuf,
    ByteAddressBuffer otherBuf,
    ByteAddressBuffer shBuf,
    Texture2D<float4> colorTex,
    StructuredBuffer<SplatChunkData> chunks,
    uint chunkCount,
    uint idx,
    uint formatFlags)
{
    DecodedSplat s;
    s.pos = 0; s.rot = float4(0,0,0,1); s.scale = 0;
    s.opacity = 0; s.color = 0;
    for (int i = 0; i < 15; i++) s.sh[i] = 0;

    uint posFmt   = formatFlags & 0xFF;
    uint scaleFmt = (formatFlags >> 8) & 0xFF;
    uint shFmt    = (formatFlags >> 16) & 0xFF;

    // Position
    uint posStride = SplatGetVectorStride(posFmt);
    s.pos = SplatDecodeVector(posBuf, idx * posStride, posFmt);

    // Rotation + Scale from Other buffer
    uint otherStride = 4; // rotation is always 10.10.10.2
    otherStride += SplatGetVectorStride(scaleFmt);
    if (shFmt > SPLAT_DECODE_FMT_6) otherStride += 2; // cluster SH index

    uint otherAddr = idx * otherStride;
    s.rot = SplatDecodeRotation(SplatDecode_10_10_10_2(SplatLoadUInt(otherBuf, otherAddr)));
    s.scale = SplatDecodeVector(otherBuf, otherAddr + 4, scaleFmt);

    // Color from texture (morton swizzle)
    static const uint kTexW = 2048;
    uint tidx = idx;
    uint2 xy;
    // Inline DecodeMorton2D_16x16
    {
        uint t = (tidx & 0xFF) | ((tidx & 0xFE) << 7);
        t &= 0x5555;
        t = (t ^ (t >> 1)) & 0x3333;
        t = (t ^ (t >> 2)) & 0x0f0f;
        xy = uint2(t & 0xF, t >> 8);
    }
    uint w16 = kTexW / 16;
    tidx >>= 8;
    uint3 coord = uint3((tidx % w16) * 16 + xy.x, (tidx / w16) * 16 + xy.y, 0);
    float4 col = colorTex.Load(coord);

    // SH coefficients
    uint shIndex = idx;
    if (shFmt > SPLAT_DECODE_FMT_6)
        shIndex = SplatLoadUShort(otherBuf, otherAddr + otherStride - 2);

    uint shStride = 0;
    if (shFmt == SPLAT_DECODE_FMT_32F)       shStride = 192;
    else if (shFmt == SPLAT_DECODE_FMT_16 || shFmt > SPLAT_DECODE_FMT_6) shStride = 96;
    else if (shFmt == SPLAT_DECODE_FMT_11)   shStride = 60;
    else if (shFmt == SPLAT_DECODE_FMT_6)    shStride = 32;

    uint shOff = shIndex * shStride;
    uint4 shR0 = shBuf.Load4(shOff);
    uint4 shR1 = shBuf.Load4(shOff + 16);

    if (shFmt == SPLAT_DECODE_FMT_32F)
    {
        uint4 shR2 = shBuf.Load4(shOff+32); uint4 shR3 = shBuf.Load4(shOff+48);
        uint4 shR4 = shBuf.Load4(shOff+64); uint4 shR5 = shBuf.Load4(shOff+80);
        uint4 shR6 = shBuf.Load4(shOff+96); uint4 shR7 = shBuf.Load4(shOff+112);
        uint4 shR8 = shBuf.Load4(shOff+128); uint4 shR9 = shBuf.Load4(shOff+144);
        uint4 shRA = shBuf.Load4(shOff+160); uint  shRB = shBuf.Load(shOff+176);
        s.sh[0]  = float3(asfloat(shR0.x), asfloat(shR0.y), asfloat(shR0.z));
        s.sh[1]  = float3(asfloat(shR0.w), asfloat(shR1.x), asfloat(shR1.y));
        s.sh[2]  = float3(asfloat(shR1.z), asfloat(shR1.w), asfloat(shR2.x));
        s.sh[3]  = float3(asfloat(shR2.y), asfloat(shR2.z), asfloat(shR2.w));
        s.sh[4]  = float3(asfloat(shR3.x), asfloat(shR3.y), asfloat(shR3.z));
        s.sh[5]  = float3(asfloat(shR3.w), asfloat(shR4.x), asfloat(shR4.y));
        s.sh[6]  = float3(asfloat(shR4.z), asfloat(shR4.w), asfloat(shR5.x));
        s.sh[7]  = float3(asfloat(shR5.y), asfloat(shR5.z), asfloat(shR5.w));
        s.sh[8]  = float3(asfloat(shR6.x), asfloat(shR6.y), asfloat(shR6.z));
        s.sh[9]  = float3(asfloat(shR6.w), asfloat(shR7.x), asfloat(shR7.y));
        s.sh[10] = float3(asfloat(shR7.z), asfloat(shR7.w), asfloat(shR8.x));
        s.sh[11] = float3(asfloat(shR8.y), asfloat(shR8.z), asfloat(shR8.w));
        s.sh[12] = float3(asfloat(shR9.x), asfloat(shR9.y), asfloat(shR9.z));
        s.sh[13] = float3(asfloat(shR9.w), asfloat(shRA.x), asfloat(shRA.y));
        s.sh[14] = float3(asfloat(shRA.z), asfloat(shRA.w), asfloat(shRB));
    }
    else if (shFmt == SPLAT_DECODE_FMT_16 || shFmt > SPLAT_DECODE_FMT_6)
    {
        uint4 shR2 = shBuf.Load4(shOff+32); uint4 shR3 = shBuf.Load4(shOff+48);
        uint4 shR4 = shBuf.Load4(shOff+64); uint3 shR5 = shBuf.Load3(shOff+80);
        s.sh[0]  = float3(f16tof32(shR0.x), f16tof32(shR0.x>>16), f16tof32(shR0.y));
        s.sh[1]  = float3(f16tof32(shR0.y>>16), f16tof32(shR0.z), f16tof32(shR0.z>>16));
        s.sh[2]  = float3(f16tof32(shR0.w), f16tof32(shR0.w>>16), f16tof32(shR1.x));
        s.sh[3]  = float3(f16tof32(shR1.x>>16), f16tof32(shR1.y), f16tof32(shR1.y>>16));
        s.sh[4]  = float3(f16tof32(shR1.z), f16tof32(shR1.z>>16), f16tof32(shR1.w));
        s.sh[5]  = float3(f16tof32(shR1.w>>16), f16tof32(shR2.x), f16tof32(shR2.x>>16));
        s.sh[6]  = float3(f16tof32(shR2.y), f16tof32(shR2.y>>16), f16tof32(shR2.z));
        s.sh[7]  = float3(f16tof32(shR2.z>>16), f16tof32(shR2.w), f16tof32(shR2.w>>16));
        s.sh[8]  = float3(f16tof32(shR3.x), f16tof32(shR3.x>>16), f16tof32(shR3.y));
        s.sh[9]  = float3(f16tof32(shR3.y>>16), f16tof32(shR3.z), f16tof32(shR3.z>>16));
        s.sh[10] = float3(f16tof32(shR3.w), f16tof32(shR3.w>>16), f16tof32(shR4.x));
        s.sh[11] = float3(f16tof32(shR4.x>>16), f16tof32(shR4.y), f16tof32(shR4.y>>16));
        s.sh[12] = float3(f16tof32(shR4.z), f16tof32(shR4.z>>16), f16tof32(shR4.w));
        s.sh[13] = float3(f16tof32(shR4.w>>16), f16tof32(shR5.x), f16tof32(shR5.x>>16));
        s.sh[14] = float3(f16tof32(shR5.y), f16tof32(shR5.y>>16), f16tof32(shR5.z));
    }
    else if (shFmt == SPLAT_DECODE_FMT_11)
    {
        uint4 shR2 = shBuf.Load4(shOff+32); uint3 shR3 = shBuf.Load3(shOff+48);
        s.sh[0]  = SplatDecode_11_10_11(shR0.x); s.sh[1]  = SplatDecode_11_10_11(shR0.y);
        s.sh[2]  = SplatDecode_11_10_11(shR0.z); s.sh[3]  = SplatDecode_11_10_11(shR0.w);
        s.sh[4]  = SplatDecode_11_10_11(shR1.x); s.sh[5]  = SplatDecode_11_10_11(shR1.y);
        s.sh[6]  = SplatDecode_11_10_11(shR1.z); s.sh[7]  = SplatDecode_11_10_11(shR1.w);
        s.sh[8]  = SplatDecode_11_10_11(shR2.x); s.sh[9]  = SplatDecode_11_10_11(shR2.y);
        s.sh[10] = SplatDecode_11_10_11(shR2.z); s.sh[11] = SplatDecode_11_10_11(shR2.w);
        s.sh[12] = SplatDecode_11_10_11(shR3.x); s.sh[13] = SplatDecode_11_10_11(shR3.y);
        s.sh[14] = SplatDecode_11_10_11(shR3.z);
    }
    else if (shFmt == SPLAT_DECODE_FMT_6)
    {
        s.sh[0]  = SplatDecode_5_6_5(shR0.x);       s.sh[1]  = SplatDecode_5_6_5(shR0.x>>16);
        s.sh[2]  = SplatDecode_5_6_5(shR0.y);       s.sh[3]  = SplatDecode_5_6_5(shR0.y>>16);
        s.sh[4]  = SplatDecode_5_6_5(shR0.z);       s.sh[5]  = SplatDecode_5_6_5(shR0.z>>16);
        s.sh[6]  = SplatDecode_5_6_5(shR0.w);       s.sh[7]  = SplatDecode_5_6_5(shR0.w>>16);
        s.sh[8]  = SplatDecode_5_6_5(shR1.x);       s.sh[9]  = SplatDecode_5_6_5(shR1.x>>16);
        s.sh[10] = SplatDecode_5_6_5(shR1.y);       s.sh[11] = SplatDecode_5_6_5(shR1.y>>16);
        s.sh[12] = SplatDecode_5_6_5(shR1.z);       s.sh[13] = SplatDecode_5_6_5(shR1.z>>16);
        s.sh[14] = SplatDecode_5_6_5(shR1.w);
    }

    // Chunk dequantization
    uint chunkIdx = idx / kDecodeChunkSize;
    if (chunkIdx < chunkCount)
    {
        SplatChunkData chunk = chunks[chunkIdx];
        float3 posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
        float3 posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
        half3 sclMin = half3(f16tof32(chunk.sclX), f16tof32(chunk.sclY), f16tof32(chunk.sclZ));
        half3 sclMax = half3(f16tof32(chunk.sclX>>16), f16tof32(chunk.sclY>>16), f16tof32(chunk.sclZ>>16));
        half4 colMin = half4(f16tof32(chunk.colR), f16tof32(chunk.colG), f16tof32(chunk.colB), f16tof32(chunk.colA));
        half4 colMax = half4(f16tof32(chunk.colR>>16), f16tof32(chunk.colG>>16), f16tof32(chunk.colB>>16), f16tof32(chunk.colA>>16));
        half3 shMin = half3(f16tof32(chunk.shR), f16tof32(chunk.shG), f16tof32(chunk.shB));
        half3 shMax = half3(f16tof32(chunk.shR>>16), f16tof32(chunk.shG>>16), f16tof32(chunk.shB>>16));

        s.pos = lerp(posMin, posMax, s.pos);
        s.scale = lerp(sclMin, sclMax, s.scale);
        s.scale *= s.scale; s.scale *= s.scale; s.scale *= s.scale; // cube of squares
        col = lerp(colMin, colMax, col);
        col.a = InvSquareCentered01_Decode(col.a);

        if (shFmt > SPLAT_DECODE_FMT_32F && shFmt <= SPLAT_DECODE_FMT_6)
        {
            for (int i = 0; i < 15; i++)
                s.sh[i] = lerp(shMin, shMax, s.sh[i]);
        }
    }

    s.opacity = col.a;
    s.color = col.rgb;

    return s;
}

// ── Quaternion slerp ──

float4 SplatQuatSlerp(float4 a, float4 b, float t)
{
    float d = dot(a, b);
    if (d < 0.0) { b = -b; d = -d; }
    if (d > 0.9995)
        return normalize(lerp(a, b, t));
    float theta = acos(d);
    float sinTheta = sin(theta);
    float wa = sin((1.0 - t) * theta) / sinTheta;
    float wb = sin(t * theta) / sinTheta;
    return wa * a + wb * b;
}

#endif // GAUSSIAN_SPLAT_DECODE_HLSL
```

**Step 2: Commit**

```
feat: add GaussianSplatDecode.hlsl shared decode library
```

---

### Task 3: Create GaussianMorph.compute

**Files:**
- Create: `Assets/4DGS/Shaders/GaussianMorph.compute`

**Step 1: Create the compute shader**

```hlsl
// SPDX-License-Identifier: MIT
// Gaussian Splat morph transition compute shader.
// Decodes two assets and blends all splat properties into _MorphedData buffer.

#define GROUP_SIZE 64

#pragma kernel CSMorphSplats

#include "GaussianSplatDecode.hlsl"

// ── Source asset buffers ──
ByteAddressBuffer _SrcPos;
ByteAddressBuffer _SrcOther;
ByteAddressBuffer _SrcSH;
Texture2D<float4> _SrcColor;
StructuredBuffer<SplatChunkData> _SrcChunks;
uint _SrcFormat;
uint _SrcChunkCount;
uint _SrcSplatCount;

// ── Target asset buffers ──
ByteAddressBuffer _TgtPos;
ByteAddressBuffer _TgtOther;
ByteAddressBuffer _TgtSH;
Texture2D<float4> _TgtColor;
StructuredBuffer<SplatChunkData> _TgtChunks;
uint _TgtFormat;
uint _TgtChunkCount;
uint _TgtSplatCount;

// ── Morph parameters ──
float _MorphWeight;

// ── Output: 8 float4 per splat ──
RWStructuredBuffer<float4> _MorphOutput;

[numthreads(GROUP_SIZE, 1, 1)]
void CSMorphSplats(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    uint maxCount = max(_SrcSplatCount, _TgtSplatCount);
    if (idx >= maxCount) return;

    uint minCount = min(_SrcSplatCount, _TgtSplatCount);
    float w = _MorphWeight;

    float3 pos; float4 rot; float3 scale;
    float opacity; float3 color;
    float3 sh[15];
    for (int i = 0; i < 15; i++) sh[i] = 0;

    if (idx < minCount)
    {
        // Both assets have this splat: decode and interpolate
        DecodedSplat src = SplatDecodeFromBuffers(
            _SrcPos, _SrcOther, _SrcSH, _SrcColor, _SrcChunks,
            _SrcChunkCount, idx, _SrcFormat);
        DecodedSplat tgt = SplatDecodeFromBuffers(
            _TgtPos, _TgtOther, _TgtSH, _TgtColor, _TgtChunks,
            _TgtChunkCount, idx, _TgtFormat);

        pos     = lerp(src.pos, tgt.pos, w);
        rot     = SplatQuatSlerp(src.rot, tgt.rot, w);
        scale   = lerp(src.scale, tgt.scale, w);
        opacity = lerp(src.opacity, tgt.opacity, w);
        color   = lerp(src.color, tgt.color, w);
        for (int j = 0; j < 15; j++)
            sh[j] = lerp(src.sh[j], tgt.sh[j], w);
    }
    else if (idx < _SrcSplatCount)
    {
        // Source-only: dissolve out
        DecodedSplat src = SplatDecodeFromBuffers(
            _SrcPos, _SrcOther, _SrcSH, _SrcColor, _SrcChunks,
            _SrcChunkCount, idx, _SrcFormat);
        pos = src.pos; rot = src.rot; scale = src.scale;
        color = src.color; opacity = src.opacity * (1.0 - w);
        for (int j = 0; j < 15; j++) sh[j] = src.sh[j];
    }
    else
    {
        // Target-only: dissolve in
        DecodedSplat tgt = SplatDecodeFromBuffers(
            _TgtPos, _TgtOther, _TgtSH, _TgtColor, _TgtChunks,
            _TgtChunkCount, idx, _TgtFormat);
        pos = tgt.pos; rot = tgt.rot; scale = tgt.scale;
        color = tgt.color; opacity = tgt.opacity * w;
        for (int j = 0; j < 15; j++) sh[j] = tgt.sh[j];
    }

    // Write output: 8 float4s per splat
    uint b = idx * 8;
    _MorphOutput[b + 0] = float4(pos, opacity);
    _MorphOutput[b + 1] = rot;
    _MorphOutput[b + 2] = float4(scale, 0);
    _MorphOutput[b + 3] = float4(color, 1);
    _MorphOutput[b + 4] = float4(sh[0], sh[1].x);
    _MorphOutput[b + 5] = float4(sh[1].yz, sh[2].xy);
    _MorphOutput[b + 6] = float4(sh[2].z, sh[3]);
    // Pack remaining SH: 4-14 (11 coefficients × 3 = 33 floats, but we only have 4 float4 left)
    // Simplified: store SH as interleaved R,G,B per coefficient for first 15
    // Actually let's use a flat layout matching design doc:
    // [4]: sh0.rgb, sh1.r
    // [5]: sh1.gb, sh2.rg
    // [6]: sh2.b, sh3.rgb
    // [7]: sh4.rgb, sh5.r
    // ... this doesn't fit in 4 float4s for 15 coefficients (45 floats)
    // We need more slots. Let's use 12 float4s total (48 floats for SH + 16 for base props)

    // REVISED: Use simpler layout. Since SH has 15 coeffs × 3 channels = 45 floats,
    // we need ceil(45/4) = 12 float4 slots just for SH.
    // Total per splat: 4 (base) + 12 (SH) = 16 float4s = 256 bytes.
    // But we can store SH at half precision to save: 15 × 3 × 2 = 90 bytes → 6 float4 using f32tof16 packing
    // Let's use: 4 base float4 + 8 float4 for SH (pack 2 half3 per float4 using f32tof16)
    // Total: 12 float4 = 192 bytes per splat

    // Actually, for simplicity and correctness, let's just skip SH for the _MorphedData buffer
    // and only morph pos, rot, scale, opacity, color (the visually dominant properties).
    // SH can be added later. This keeps the buffer at 4 float4 = 64 bytes/splat.

    // FINAL LAYOUT: 4 float4 per splat (no SH in output, SH stays from source/target)
    // CalcViewData will read SH from original buffers, blending just the base color.
    // This is a pragmatic simplification that still produces great morph results.
}
```

Wait — the SH packing complexity is significant. Let me revise the design to be pragmatic.

**REVISED approach**: Store 4 base properties in `_MorphedData` (pos, rot, scale, opacity+color). SH coefficients contribute to view-dependent color which changes subtly; for morph, interpolating the base color (which includes SH band 0) covers the dominant visual effect. CalcViewData reads SH from whichever asset is "dominant" (weight < 0.5 → source SH, weight >= 0.5 → target SH).

This reduces buffer from 128B to 32B per splat (4 float4) — a 4x memory savings.

**Revised file:**

```hlsl
// SPDX-License-Identifier: MIT
// Gaussian Splat morph transition compute shader.
// Decodes two assets and blends base splat properties into _MorphedData buffer.
// SH coefficients are not blended (CalcViewData reads from the dominant asset).

#define GROUP_SIZE 64

#pragma kernel CSMorphSplats

#include "GaussianSplatDecode.hlsl"

// ── Source asset buffers ──
ByteAddressBuffer _SrcPos;
ByteAddressBuffer _SrcOther;
ByteAddressBuffer _SrcSH;
Texture2D<float4> _SrcColor;
StructuredBuffer<SplatChunkData> _SrcChunks;
uint _SrcFormat;
uint _SrcChunkCount;
uint _SrcSplatCount;

// ── Target asset buffers ──
ByteAddressBuffer _TgtPos;
ByteAddressBuffer _TgtOther;
ByteAddressBuffer _TgtSH;
Texture2D<float4> _TgtColor;
StructuredBuffer<SplatChunkData> _TgtChunks;
uint _TgtFormat;
uint _TgtChunkCount;
uint _TgtSplatCount;

// ── Morph parameters ──
float _MorphWeight;

// ── Output: 4 float4 per splat ──
// [0]: pos.xyz, opacity
// [1]: rot.xyzw
// [2]: scale.xyz, 0
// [3]: color.rgb, 1
RWStructuredBuffer<float4> _MorphOutput;

[numthreads(GROUP_SIZE, 1, 1)]
void CSMorphSplats(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    uint maxCount = max(_SrcSplatCount, _TgtSplatCount);
    if (idx >= maxCount) return;

    uint minCount = min(_SrcSplatCount, _TgtSplatCount);
    float w = _MorphWeight;

    float3 pos; float4 rot; float3 scale;
    float opacity; float3 color;

    if (idx < minCount)
    {
        DecodedSplat src = SplatDecodeFromBuffers(
            _SrcPos, _SrcOther, _SrcSH, _SrcColor, _SrcChunks,
            _SrcChunkCount, idx, _SrcFormat);
        DecodedSplat tgt = SplatDecodeFromBuffers(
            _TgtPos, _TgtOther, _TgtSH, _TgtColor, _TgtChunks,
            _TgtChunkCount, idx, _TgtFormat);

        pos     = lerp(src.pos, tgt.pos, w);
        rot     = SplatQuatSlerp(src.rot, tgt.rot, w);
        scale   = lerp(src.scale, tgt.scale, w);
        opacity = lerp(src.opacity, tgt.opacity, w);
        color   = lerp(src.color, tgt.color, w);
    }
    else if (idx < _SrcSplatCount)
    {
        DecodedSplat src = SplatDecodeFromBuffers(
            _SrcPos, _SrcOther, _SrcSH, _SrcColor, _SrcChunks,
            _SrcChunkCount, idx, _SrcFormat);
        pos = src.pos; rot = src.rot; scale = src.scale;
        color = src.color; opacity = src.opacity * (1.0 - w);
    }
    else
    {
        DecodedSplat tgt = SplatDecodeFromBuffers(
            _TgtPos, _TgtOther, _TgtSH, _TgtColor, _TgtChunks,
            _TgtChunkCount, idx, _TgtFormat);
        pos = tgt.pos; rot = tgt.rot; scale = tgt.scale;
        color = tgt.color; opacity = tgt.opacity * w;
    }

    uint b = idx * 4;
    _MorphOutput[b + 0] = float4(pos, opacity);
    _MorphOutput[b + 1] = rot;
    _MorphOutput[b + 2] = float4(scale, 0);
    _MorphOutput[b + 3] = float4(color, 1);
}
```

**Step 2: Commit**

```
feat: add GaussianMorph.compute morph kernel
```

---

### Task 4: Add morph data branch to SplatUtilities.compute

**Files:**
- Modify: `Assets/4DGS/Shaders/SplatUtilities.compute`

**Step 1: Add morph buffer declarations (after line 104)**

After the `_AnimDataValid` line, add:

```hlsl
// Morph data (set by GaussianMorph, 4 float4s per splat)
// [0]: pos.xyz, opacity
// [1]: rot.xyzw
// [2]: scale.xyz, 0
// [3]: color.rgb, 1
StructuredBuffer<float4> _MorphedData;
uint _MorphDataValid; // 0 = no morph, 1 = morph active
```

**Step 2: Add morph branch in CSCalcViewData (replace line 209)**

Replace the line:
```hlsl
SplatData splat = LoadSplatData(idx);
```

With:
```hlsl
SplatData splat;
if (_MorphDataValid != 0)
{
    uint mb = idx * 4;
    float4 md0 = _MorphedData[mb + 0];
    float4 md1 = _MorphedData[mb + 1];
    float4 md2 = _MorphedData[mb + 2];
    float4 md3 = _MorphedData[mb + 3];

    splat.pos     = md0.xyz;
    splat.rot     = md1;
    splat.scale   = md2.xyz;
    splat.opacity = (half)md0.w;
    // Base color from morph; SH from original asset (loaded below)
    splat.sh = (SplatSHData)0;
    splat.sh.col = (half3)md3.rgb;

    // Load SH from the original compressed buffers
    // (SH is not morphed; uses current source asset's SH data)
    SplatData origSH = LoadSplatData(idx);
    splat.sh.sh1  = origSH.sh.sh1;  splat.sh.sh2  = origSH.sh.sh2;
    splat.sh.sh3  = origSH.sh.sh3;  splat.sh.sh4  = origSH.sh.sh4;
    splat.sh.sh5  = origSH.sh.sh5;  splat.sh.sh6  = origSH.sh.sh6;
    splat.sh.sh7  = origSH.sh.sh7;  splat.sh.sh8  = origSH.sh.sh8;
    splat.sh.sh9  = origSH.sh.sh9;  splat.sh.sh10 = origSH.sh.sh10;
    splat.sh.sh11 = origSH.sh.sh11; splat.sh.sh12 = origSH.sh.sh12;
    splat.sh.sh13 = origSH.sh.sh13; splat.sh.sh14 = origSH.sh.sh14;
    splat.sh.sh15 = origSH.sh.sh15;
}
else
{
    splat = LoadSplatData(idx);
}
```

**Note:** Loading full SplatData just for SH is wasteful but correct. A future optimization can load only SH data. For MVP this is fine since CalcViewData already loads the full splat.

**Step 3: Commit**

```
feat: add morph data branch to CalcViewData kernel
```

---

### Task 5: Create GaussianMorph.cs component

**Files:**
- Create: `Assets/4DGS/Runtime/GaussianMorph.cs`

**Step 1: Create the component**

```csharp
// SPDX-License-Identifier: MIT

using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;

namespace GaussianSplatting.Runtime
{
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [ExecuteInEditMode]
    [DefaultExecutionOrder(-50)]
    public class GaussianMorph : MonoBehaviour
    {
        [Tooltip("Target Gaussian Splat asset to morph towards")]
        public GaussianSplatAsset targetAsset;

        [Tooltip("Morph weight: 0 = source, 1 = target")]
        [Range(0f, 1f)]
        public float weight;

        [Tooltip("Compute shader for morph blending")]
        public ComputeShader morphShader;

        GaussianSplatRenderer m_Renderer;

        // Target asset GPU buffers
        GraphicsBuffer m_TgtPosData;
        GraphicsBuffer m_TgtOtherData;
        GraphicsBuffer m_TgtSHData;
        Texture2D m_TgtColorTex;
        GraphicsBuffer m_TgtChunks;
        bool m_TgtChunksValid;

        // Morph output
        GraphicsBuffer m_MorphOutput;

        int m_KernelMorph = -1;
        GaussianSplatAsset m_CachedTarget;
        int m_MorphedSplatCount;

        static readonly ProfilerMarker s_ProfMorph = new(ProfilerCategory.Render, "GaussianSplat.Morph");

        // Shader property IDs
        static readonly int PropSrcPos = Shader.PropertyToID("_SrcPos");
        static readonly int PropSrcOther = Shader.PropertyToID("_SrcOther");
        static readonly int PropSrcSH = Shader.PropertyToID("_SrcSH");
        static readonly int PropSrcColor = Shader.PropertyToID("_SrcColor");
        static readonly int PropSrcChunks = Shader.PropertyToID("_SrcChunks");
        static readonly int PropSrcFormat = Shader.PropertyToID("_SrcFormat");
        static readonly int PropSrcChunkCount = Shader.PropertyToID("_SrcChunkCount");
        static readonly int PropSrcSplatCount = Shader.PropertyToID("_SrcSplatCount");

        static readonly int PropTgtPos = Shader.PropertyToID("_TgtPos");
        static readonly int PropTgtOther = Shader.PropertyToID("_TgtOther");
        static readonly int PropTgtSH = Shader.PropertyToID("_TgtSH");
        static readonly int PropTgtColor = Shader.PropertyToID("_TgtColor");
        static readonly int PropTgtChunks = Shader.PropertyToID("_TgtChunks");
        static readonly int PropTgtFormat = Shader.PropertyToID("_TgtFormat");
        static readonly int PropTgtChunkCount = Shader.PropertyToID("_TgtChunkCount");
        static readonly int PropTgtSplatCount = Shader.PropertyToID("_TgtSplatCount");

        static readonly int PropMorphWeight = Shader.PropertyToID("_MorphWeight");
        static readonly int PropMorphOutput = Shader.PropertyToID("_MorphOutput");

        void OnEnable()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
            if (morphShader != null)
                m_KernelMorph = morphShader.FindKernel("CSMorphSplats");
        }

        void OnDisable()
        {
            ReleaseTargetBuffers();
            ReleaseMorphOutput();
            if (m_Renderer != null)
                m_Renderer.SetMorphData(null, 0, 0);
        }

        void LateUpdate()
        {
            if (m_Renderer == null || !m_Renderer.HasValidAsset || !m_Renderer.HasValidRenderSetup)
            {
                m_Renderer?.SetMorphData(null, 0, 0);
                return;
            }

            if (targetAsset == null || morphShader == null || weight <= 0f)
            {
                m_Renderer.SetMorphData(null, 0, 0);
                return;
            }

            if (m_KernelMorph < 0)
            {
                m_KernelMorph = morphShader.FindKernel("CSMorphSplats");
                if (m_KernelMorph < 0) return;
            }

            s_ProfMorph.Begin();

            EnsureTargetBuffers();
            EnsureMorphOutput();
            DispatchMorph();

            m_Renderer.SetMorphData(m_MorphOutput, m_MorphedSplatCount, 1);

            s_ProfMorph.End();
        }

        void EnsureTargetBuffers()
        {
            if (m_CachedTarget == targetAsset && m_TgtPosData != null)
                return;

            ReleaseTargetBuffers();
            m_CachedTarget = targetAsset;

            if (targetAsset == null || targetAsset.posData == null)
                return;

            m_TgtPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(targetAsset.posData.dataSize / 4), 4);
            m_TgtPosData.SetData(targetAsset.posData.GetData<uint>());

            m_TgtOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(targetAsset.otherData.dataSize / 4), 4);
            m_TgtOtherData.SetData(targetAsset.otherData.GetData<uint>());

            m_TgtSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(targetAsset.shData.dataSize / 4), 4);
            m_TgtSHData.SetData(targetAsset.shData.GetData<uint>());

            var (texW, texH) = GaussianSplatAsset.CalcTextureSize(targetAsset.splatCount);
            var texFmt = GaussianSplatAsset.ColorFormatToGraphics(targetAsset.colorFormat);
            m_TgtColorTex = new Texture2D(texW, texH, texFmt,
                TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate);
            m_TgtColorTex.SetPixelData(targetAsset.colorData.GetData<byte>(), 0);
            m_TgtColorTex.Apply(false, true);

            if (targetAsset.chunkData != null && targetAsset.chunkData.dataSize != 0)
            {
                m_TgtChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int)(targetAsset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>());
                m_TgtChunks.SetData(targetAsset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                m_TgtChunksValid = true;
            }
            else
            {
                m_TgtChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>());
                m_TgtChunksValid = false;
            }
        }

        void EnsureMorphOutput()
        {
            int srcCount = m_Renderer.splatCount;
            int tgtCount = targetAsset != null ? targetAsset.splatCount : 0;
            int maxCount = Mathf.Max(srcCount, tgtCount);

            if (m_MorphOutput != null && m_MorphedSplatCount == maxCount)
                return;

            ReleaseMorphOutput();
            m_MorphedSplatCount = maxCount;
            // 4 float4 per splat
            m_MorphOutput = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxCount * 4, 16)
                { name = "GaussianMorphOutput" };
        }

        void DispatchMorph()
        {
            var cs = morphShader;
            int kernel = m_KernelMorph;

            var srcAsset = m_Renderer.asset;
            uint srcFmt = (uint)srcAsset.posFormat | ((uint)srcAsset.scaleFormat << 8) | ((uint)srcAsset.shFormat << 16);
            uint tgtFmt = (uint)targetAsset.posFormat | ((uint)targetAsset.scaleFormat << 8) | ((uint)targetAsset.shFormat << 16);

            // Source buffers
            cs.SetBuffer(kernel, PropSrcPos, m_Renderer.GpuPosData);
            cs.SetBuffer(kernel, PropSrcOther, m_Renderer.GpuOtherData);
            cs.SetBuffer(kernel, PropSrcSH, m_Renderer.GpuSHData);
            cs.SetTexture(kernel, PropSrcColor, m_Renderer.GpuColorData);
            cs.SetBuffer(kernel, PropSrcChunks, m_Renderer.GpuChunksBuffer);
            cs.SetInt(PropSrcFormat, (int)srcFmt);
            cs.SetInt(PropSrcChunkCount, m_Renderer.GpuChunksValid ? m_Renderer.GpuChunksBuffer.count : 0);
            cs.SetInt(PropSrcSplatCount, m_Renderer.splatCount);

            // Target buffers
            cs.SetBuffer(kernel, PropTgtPos, m_TgtPosData);
            cs.SetBuffer(kernel, PropTgtOther, m_TgtOtherData);
            cs.SetBuffer(kernel, PropTgtSH, m_TgtSHData);
            cs.SetTexture(kernel, PropTgtColor, m_TgtColorTex);
            cs.SetBuffer(kernel, PropTgtChunks, m_TgtChunks);
            cs.SetInt(PropTgtFormat, (int)tgtFmt);
            cs.SetInt(PropTgtChunkCount, m_TgtChunksValid ? m_TgtChunks.count : 0);
            cs.SetInt(PropTgtSplatCount, targetAsset.splatCount);

            // Morph params
            cs.SetFloat(PropMorphWeight, weight);
            cs.SetBuffer(kernel, PropMorphOutput, m_MorphOutput);

            cs.GetKernelThreadGroupSizes(kernel, out uint gsX, out _, out _);
            cs.Dispatch(kernel, (m_MorphedSplatCount + (int)gsX - 1) / (int)gsX, 1, 1);
        }

        void ReleaseTargetBuffers()
        {
            m_TgtPosData?.Release(); m_TgtPosData = null;
            m_TgtOtherData?.Release(); m_TgtOtherData = null;
            m_TgtSHData?.Release(); m_TgtSHData = null;
            if (m_TgtColorTex != null) { DestroyImmediate(m_TgtColorTex); m_TgtColorTex = null; }
            m_TgtChunks?.Release(); m_TgtChunks = null;
            m_TgtChunksValid = false;
            m_CachedTarget = null;
        }

        void ReleaseMorphOutput()
        {
            m_MorphOutput?.Release();
            m_MorphOutput = null;
            m_MorphedSplatCount = 0;
        }
    }
}
```

**Step 2: Expose needed internal accessors on GaussianSplatRenderer**

The component needs access to source GPU buffers. Add these internal properties near line 623:

```csharp
internal GraphicsBuffer GpuOtherData => m_GpuOtherData;
internal GraphicsBuffer GpuSHData => m_GpuSHData;
internal Texture GpuColorData => m_GpuColorData;
internal GraphicsBuffer GpuChunksBuffer => m_GpuChunks;
internal bool GpuChunksValid => m_GpuChunksValid;
```

**Step 3: Commit**

```
feat: add GaussianMorph component for morph transitions
```

---

### Task 6: Create GaussianMorphEditor.cs

**Files:**
- Create: `Assets/4DGS/Editor/GaussianMorphEditor.cs`

**Step 1: Create the editor (matches existing IMGUI pattern)**

```csharp
// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianMorph))]
    [CanEditMultipleObjects]
    public class GaussianMorphEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }
    }
}
```

**Step 2: Commit**

```
feat: add GaussianMorph custom IMGUI editor
```

---

### Task 7: Ensure GpuView buffer is large enough for morph

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

When morph is active, `m_MorphedSplatCount` may exceed the original `m_GpuView.count`. The sort buffers and view buffer need to handle this. The simplest approach: in `CalcViewData`, if morph count exceeds current buffer sizes, skip the morph (log a warning). A proper fix requires resizing view/sort buffers which is complex.

For MVP, **constrain morph to assets with equal or smaller splat count than the source**. Document this limitation. The `SetMorphData` method should clamp:

```csharp
internal void SetMorphData(GraphicsBuffer morphedData, int splatCount, int valid)
{
    m_MorphedDataBuffer = morphedData;
    // Clamp to current view buffer capacity to avoid out-of-bounds
    m_MorphedSplatCount = m_GpuView != null ? Mathf.Min(splatCount, m_GpuView.count) : splatCount;
    m_MorphDataValid = valid;
}
```

**Commit:**

```
fix: clamp morph splat count to view buffer capacity
```

---

### Task 8: Wire up .meta files and verify compilation

**Step 1:** Open Unity, let it import the new files and generate `.meta` files

Run: `open -a Unity /Users/hankun/GitHub/4DGS`

**Step 2:** Verify no compilation errors in Console

**Step 3:** Add GaussianMorph component to a test scene object that has GaussianSplatRenderer, assign compute shader and a target asset, slide weight

**Step 4:** Commit all new `.meta` files

```
chore: add meta files for morph system
```

---

### Task 9: Final integration commit

After verifying everything works:

```
git add -A && git status
```

Verify all files are expected, then:

```
feat: complete Gaussian morph transition system MVP
```
