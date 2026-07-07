// SPDX-License-Identifier: MIT
// Shared decode library for Gaussian Splat data.
// Provides parameterized decode functions that accept explicit buffer arguments,
// enabling both GaussianAnimate.compute and GaussianMorph.compute to decode
// splat data from arbitrary buffers without global buffer dependencies.

#ifndef GAUSSIAN_SPLAT_DECODE_HLSL
#define GAUSSIAN_SPLAT_DECODE_HLSL

// ── Vector format constants (must match GaussianSplatAsset.VectorFormat) ──

#define SPLAT_FMT_32F 0
#define SPLAT_FMT_16  1
#define SPLAT_FMT_11  2
#define SPLAT_FMT_6   3

static const uint kSplatChunkSize = 256;
static const uint kSplatTexWidth = 2048;

// ── Chunk data struct (matches SplatChunkInfo / AnimSplatChunkInfo layout) ──

struct SplatChunkData
{
    uint colR, colG, colB, colA;
    float2 posX, posY, posZ;
    uint sclX, sclY, sclZ;
    uint shR, shG, shB;
};

// ── Decoded splat struct ──

struct DecodedSplat
{
    float3 pos;
    float4 rot;
    float3 scale;
    half   opacity;
    half3  color;
    half3  sh[15];
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

// ── Buffer load helpers (handle unaligned access from ByteAddressBuffer) ──

uint SplatLoadUShort(ByteAddressBuffer buf, uint addrU)
{
    uint addrA = addrU & ~0x3;
    uint val = buf.Load(addrA);
    if (addrU != addrA)
        val >>= 16;
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

// ── Vector decode with unaligned access support ──

float3 SplatDecodeVector(ByteAddressBuffer buf, uint addrU, uint fmt)
{
    uint addrA = addrU & ~0x3;
    uint val0 = buf.Load(addrA);

    float3 res = 0;
    if (fmt == SPLAT_FMT_32F)
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
    else if (fmt == SPLAT_FMT_16)
    {
        uint val1 = buf.Load(addrA + 4);
        if (addrU != addrA)
        {
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
            val1 >>= 16;
        }
        res = SplatDecode_16_16_16(uint2(val0, val1));
    }
    else if (fmt == SPLAT_FMT_11)
    {
        uint val1 = buf.Load(addrA + 4);
        if (addrU != addrA)
        {
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
        }
        res = SplatDecode_11_10_11(val0);
    }
    else if (fmt == SPLAT_FMT_6)
    {
        if (addrU != addrA)
            val0 >>= 16;
        res = SplatDecode_6_5_5(val0);
    }
    return res;
}

// ── Vector stride for a given format ──

uint SplatGetVectorStride(uint fmt)
{
    if (fmt == SPLAT_FMT_32F) return 12;
    if (fmt == SPLAT_FMT_16)  return 6;
    if (fmt == SPLAT_FMT_11)  return 4;
    return 2; // SPLAT_FMT_6
}

// ── Quaternion decode from "smallest 3" 10.10.10.2 packed format ──

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

// ── Opacity dequantization ──

float SplatInvSquareCentered01(float x)
{
    x -= 0.5;
    x *= 0.5;
    x = sqrt(abs(x)) * sign(x);
    return x + 0.5;
}

// ── Morton interleaving for color texture indexing ──

uint2 SplatDecodeMorton2D_16x16(uint t)
{
    t = (t & 0xFF) | ((t & 0xFE) << 7);
    t &= 0x5555;
    t = (t ^ (t >> 1)) & 0x3333;
    t = (t ^ (t >> 2)) & 0x0f0f;
    return uint2(t & 0xF, t >> 8);
}

uint3 SplatIndexToPixelIndex(uint idx)
{
    uint3 res;
    uint2 xy = SplatDecodeMorton2D_16x16(idx);
    uint width = kSplatTexWidth / 16;
    idx >>= 8;
    res.x = (idx % width) * 16 + xy.x;
    res.y = (idx / width) * 16 + xy.y;
    res.z = 0;
    return res;
}

// ── Quaternion spherical linear interpolation ──

float4 SplatQuatSlerp(float4 a, float4 b, float t)
{
    // Ensure shortest path
    float d = dot(a, b);
    if (d < 0.0)
    {
        b = -b;
        d = -d;
    }

    // Fall back to nlerp for very close quaternions to avoid division by zero
    if (d > 0.9995)
    {
        float4 result = lerp(a, b, t);
        return normalize(result);
    }

    float theta = acos(clamp(d, -1.0, 1.0));
    float sinTheta = sin(theta);
    float wa = sin((1.0 - t) * theta) / sinTheta;
    float wb = sin(t * theta) / sinTheta;
    return a * wa + b * wb;
}

// ── Full splat decode from explicit buffer arguments ──
//
// Parameters:
//   posBuf      - ByteAddressBuffer containing splat position data
//   otherBuf    - ByteAddressBuffer containing rotation, scale, and optional SH cluster index
//   shBuf       - ByteAddressBuffer containing spherical harmonics data
//   col         - raw color texel, loaded by the caller via
//                 colorTex.Load(SplatIndexToPixelIndex(idx)). Textures must not
//                 be function parameters: the HLSL->SPIR-V/GLSL translation
//                 silently returns wrong values for parameterized texture loads
//                 on some platforms (colors decode as chunk colMax = washed-out
//                 white), while buffer parameters translate fine.
//   chunks      - StructuredBuffer of SplatChunkData for dequantization
//   chunkCount  - number of valid chunks
//   idx         - splat index
//   formatFlags - packed format: bits [0..7] = posFmt, [8..15] = scaleFmt, [16..23] = shFormat

DecodedSplat SplatDecodeFromBuffers(
    ByteAddressBuffer posBuf,
    ByteAddressBuffer otherBuf,
    ByteAddressBuffer shBuf,
    half4 col,
    StructuredBuffer<SplatChunkData> chunks,
    uint chunkCount,
    uint idx,
    uint formatFlags)
{
    DecodedSplat s = (DecodedSplat)0;

    // Extract per-component formats
    uint posFmt   = formatFlags & 0xFF;
    uint scaleFmt = (formatFlags >> 8) & 0xFF;
    uint shFormat = (formatFlags >> 16) & 0xFF;

    // -- Calculate strides and offsets --

    uint posStride = SplatGetVectorStride(posFmt);
    uint scaleStride = SplatGetVectorStride(scaleFmt);

    // Other buffer layout: rotation (4 bytes, 10.10.10.2) + scale + optional SH cluster index (2 bytes)
    uint otherStride = 4 + scaleStride;
    if (shFormat > SPLAT_FMT_6)
        otherStride += 2;
    uint otherAddr = idx * otherStride;

    // SH stride per splat/cluster
    uint shStride = 0;
    if (shFormat == SPLAT_FMT_32F)
        shStride = 192; // 15*3 fp32, rounded up to multiple of 16
    else if (shFormat == SPLAT_FMT_16 || shFormat > SPLAT_FMT_6)
        shStride = 96;  // 15*3 fp16, rounded up to multiple of 16
    else if (shFormat == SPLAT_FMT_11)
        shStride = 60;  // 15x uint
    else if (shFormat == SPLAT_FMT_6)
        shStride = 32;  // 15x ushort, rounded up to multiple of 4

    // -- Load raw data --

    // Position
    s.pos = SplatDecodeVector(posBuf, idx * posStride, posFmt);

    // Rotation: 10.10.10.2 packed quaternion
    s.rot = SplatDecodeRotation(SplatDecode_10_10_10_2(SplatLoadUInt(otherBuf, otherAddr)));

    // Scale
    s.scale = SplatDecodeVector(otherBuf, otherAddr + 4, scaleFmt);

    // SH cluster index (if clustered SH format)
    uint shIndex = idx;
    if (shFormat > SPLAT_FMT_6)
        shIndex = SplatLoadUShort(otherBuf, otherAddr + otherStride - 2);

    // -- Load SH data --

    uint shOffset = shIndex * shStride;

    if (shStride > 0)
    {
        uint4 shRaw0 = shBuf.Load4(shOffset);
        uint4 shRaw1 = shBuf.Load4(shOffset + 16);

        if (shFormat == SPLAT_FMT_32F)
        {
            uint4 shRaw2 = shBuf.Load4(shOffset + 32);
            uint4 shRaw3 = shBuf.Load4(shOffset + 48);
            uint4 shRaw4 = shBuf.Load4(shOffset + 64);
            uint4 shRaw5 = shBuf.Load4(shOffset + 80);
            uint4 shRaw6 = shBuf.Load4(shOffset + 96);
            uint4 shRaw7 = shBuf.Load4(shOffset + 112);
            uint4 shRaw8 = shBuf.Load4(shOffset + 128);
            uint4 shRaw9 = shBuf.Load4(shOffset + 144);
            uint4 shRawA = shBuf.Load4(shOffset + 160);
            uint  shRawB = shBuf.Load(shOffset + 176);
            s.sh[0]  = half3(asfloat(shRaw0.x), asfloat(shRaw0.y), asfloat(shRaw0.z));
            s.sh[1]  = half3(asfloat(shRaw0.w), asfloat(shRaw1.x), asfloat(shRaw1.y));
            s.sh[2]  = half3(asfloat(shRaw1.z), asfloat(shRaw1.w), asfloat(shRaw2.x));
            s.sh[3]  = half3(asfloat(shRaw2.y), asfloat(shRaw2.z), asfloat(shRaw2.w));
            s.sh[4]  = half3(asfloat(shRaw3.x), asfloat(shRaw3.y), asfloat(shRaw3.z));
            s.sh[5]  = half3(asfloat(shRaw3.w), asfloat(shRaw4.x), asfloat(shRaw4.y));
            s.sh[6]  = half3(asfloat(shRaw4.z), asfloat(shRaw4.w), asfloat(shRaw5.x));
            s.sh[7]  = half3(asfloat(shRaw5.y), asfloat(shRaw5.z), asfloat(shRaw5.w));
            s.sh[8]  = half3(asfloat(shRaw6.x), asfloat(shRaw6.y), asfloat(shRaw6.z));
            s.sh[9]  = half3(asfloat(shRaw6.w), asfloat(shRaw7.x), asfloat(shRaw7.y));
            s.sh[10] = half3(asfloat(shRaw7.z), asfloat(shRaw7.w), asfloat(shRaw8.x));
            s.sh[11] = half3(asfloat(shRaw8.y), asfloat(shRaw8.z), asfloat(shRaw8.w));
            s.sh[12] = half3(asfloat(shRaw9.x), asfloat(shRaw9.y), asfloat(shRaw9.z));
            s.sh[13] = half3(asfloat(shRaw9.w), asfloat(shRawA.x), asfloat(shRawA.y));
            s.sh[14] = half3(asfloat(shRawA.z), asfloat(shRawA.w), asfloat(shRawB));
        }
        else if (shFormat == SPLAT_FMT_16 || shFormat > SPLAT_FMT_6)
        {
            uint4 shRaw2 = shBuf.Load4(shOffset + 32);
            uint4 shRaw3 = shBuf.Load4(shOffset + 48);
            uint4 shRaw4 = shBuf.Load4(shOffset + 64);
            uint3 shRaw5 = shBuf.Load3(shOffset + 80);
            s.sh[0]  = half3(f16tof32(shRaw0.x      ), f16tof32(shRaw0.x >> 16), f16tof32(shRaw0.y      ));
            s.sh[1]  = half3(f16tof32(shRaw0.y >> 16), f16tof32(shRaw0.z      ), f16tof32(shRaw0.z >> 16));
            s.sh[2]  = half3(f16tof32(shRaw0.w      ), f16tof32(shRaw0.w >> 16), f16tof32(shRaw1.x      ));
            s.sh[3]  = half3(f16tof32(shRaw1.x >> 16), f16tof32(shRaw1.y      ), f16tof32(shRaw1.y >> 16));
            s.sh[4]  = half3(f16tof32(shRaw1.z      ), f16tof32(shRaw1.z >> 16), f16tof32(shRaw1.w      ));
            s.sh[5]  = half3(f16tof32(shRaw1.w >> 16), f16tof32(shRaw2.x      ), f16tof32(shRaw2.x >> 16));
            s.sh[6]  = half3(f16tof32(shRaw2.y      ), f16tof32(shRaw2.y >> 16), f16tof32(shRaw2.z      ));
            s.sh[7]  = half3(f16tof32(shRaw2.z >> 16), f16tof32(shRaw2.w      ), f16tof32(shRaw2.w >> 16));
            s.sh[8]  = half3(f16tof32(shRaw3.x      ), f16tof32(shRaw3.x >> 16), f16tof32(shRaw3.y      ));
            s.sh[9]  = half3(f16tof32(shRaw3.y >> 16), f16tof32(shRaw3.z      ), f16tof32(shRaw3.z >> 16));
            s.sh[10] = half3(f16tof32(shRaw3.w      ), f16tof32(shRaw3.w >> 16), f16tof32(shRaw4.x      ));
            s.sh[11] = half3(f16tof32(shRaw4.x >> 16), f16tof32(shRaw4.y      ), f16tof32(shRaw4.y >> 16));
            s.sh[12] = half3(f16tof32(shRaw4.z      ), f16tof32(shRaw4.z >> 16), f16tof32(shRaw4.w      ));
            s.sh[13] = half3(f16tof32(shRaw4.w >> 16), f16tof32(shRaw5.x      ), f16tof32(shRaw5.x >> 16));
            s.sh[14] = half3(f16tof32(shRaw5.y      ), f16tof32(shRaw5.y >> 16), f16tof32(shRaw5.z      ));
        }
        else if (shFormat == SPLAT_FMT_11)
        {
            uint4 shRaw2 = shBuf.Load4(shOffset + 32);
            uint3 shRaw3 = shBuf.Load3(shOffset + 48);
            s.sh[0]  = SplatDecode_11_10_11(shRaw0.x);
            s.sh[1]  = SplatDecode_11_10_11(shRaw0.y);
            s.sh[2]  = SplatDecode_11_10_11(shRaw0.z);
            s.sh[3]  = SplatDecode_11_10_11(shRaw0.w);
            s.sh[4]  = SplatDecode_11_10_11(shRaw1.x);
            s.sh[5]  = SplatDecode_11_10_11(shRaw1.y);
            s.sh[6]  = SplatDecode_11_10_11(shRaw1.z);
            s.sh[7]  = SplatDecode_11_10_11(shRaw1.w);
            s.sh[8]  = SplatDecode_11_10_11(shRaw2.x);
            s.sh[9]  = SplatDecode_11_10_11(shRaw2.y);
            s.sh[10] = SplatDecode_11_10_11(shRaw2.z);
            s.sh[11] = SplatDecode_11_10_11(shRaw2.w);
            s.sh[12] = SplatDecode_11_10_11(shRaw3.x);
            s.sh[13] = SplatDecode_11_10_11(shRaw3.y);
            s.sh[14] = SplatDecode_11_10_11(shRaw3.z);
        }
        else if (shFormat == SPLAT_FMT_6)
        {
            s.sh[0]  = SplatDecode_5_6_5(shRaw0.x);
            s.sh[1]  = SplatDecode_5_6_5(shRaw0.x >> 16);
            s.sh[2]  = SplatDecode_5_6_5(shRaw0.y);
            s.sh[3]  = SplatDecode_5_6_5(shRaw0.y >> 16);
            s.sh[4]  = SplatDecode_5_6_5(shRaw0.z);
            s.sh[5]  = SplatDecode_5_6_5(shRaw0.z >> 16);
            s.sh[6]  = SplatDecode_5_6_5(shRaw0.w);
            s.sh[7]  = SplatDecode_5_6_5(shRaw0.w >> 16);
            s.sh[8]  = SplatDecode_5_6_5(shRaw1.x);
            s.sh[9]  = SplatDecode_5_6_5(shRaw1.x >> 16);
            s.sh[10] = SplatDecode_5_6_5(shRaw1.y);
            s.sh[11] = SplatDecode_5_6_5(shRaw1.y >> 16);
            s.sh[12] = SplatDecode_5_6_5(shRaw1.z);
            s.sh[13] = SplatDecode_5_6_5(shRaw1.z >> 16);
            s.sh[14] = SplatDecode_5_6_5(shRaw1.w);
        }
    }

    // -- Chunk dequantization --

    uint chunkIdx = idx / kSplatChunkSize;
    if (chunkIdx < chunkCount)
    {
        SplatChunkData chunk = chunks[chunkIdx];

        // Position dequantization
        float3 posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
        float3 posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
        s.pos = lerp(posMin, posMax, s.pos);

        // Scale dequantization
        half3 sclMin = half3(f16tof32(chunk.sclX    ), f16tof32(chunk.sclY    ), f16tof32(chunk.sclZ    ));
        half3 sclMax = half3(f16tof32(chunk.sclX>>16), f16tof32(chunk.sclY>>16), f16tof32(chunk.sclZ>>16));
        s.scale = lerp(sclMin, sclMax, s.scale);
        // Cube of squares: scale = scale^8
        s.scale *= s.scale;
        s.scale *= s.scale;
        s.scale *= s.scale;

        // Color dequantization
        half4 colMin = half4(f16tof32(chunk.colR    ), f16tof32(chunk.colG    ), f16tof32(chunk.colB    ), f16tof32(chunk.colA    ));
        half4 colMax = half4(f16tof32(chunk.colR>>16), f16tof32(chunk.colG>>16), f16tof32(chunk.colB>>16), f16tof32(chunk.colA>>16));
        col = lerp(colMin, colMax, col);
        col.a = SplatInvSquareCentered01(col.a);

        // SH dequantization (only for quantized non-float32 formats that are standard, not cluster)
        if (shFormat > SPLAT_FMT_32F && shFormat <= SPLAT_FMT_6)
        {
            half3 shMin = half3(f16tof32(chunk.shR    ), f16tof32(chunk.shG    ), f16tof32(chunk.shB    ));
            half3 shMax = half3(f16tof32(chunk.shR>>16), f16tof32(chunk.shG>>16), f16tof32(chunk.shB>>16));
            [unroll]
            for (int i = 0; i < 15; i++)
                s.sh[i] = lerp(shMin, shMax, s.sh[i]);
        }
    }

    s.opacity = col.a;
    s.color   = col.rgb;

    return s;
}

#endif // GAUSSIAN_SPLAT_DECODE_HLSL
