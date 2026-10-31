// SPDX-License-Identifier: MIT
#ifndef GAUSSIAN_SPLATTING_HLSL
#define GAUSSIAN_SPLATTING_HLSL

#include "GaussianSplatFormat.hlsl"

float InvSquareCentered01(float x)
{
    x -= 0.5;
    x *= 0.5;
    x = sqrt(abs(x)) * sign(x);
    return x + 0.5;
}

float3 QuatRotateVector(float3 v, float4 r)
{
    float3 t = 2 * cross(r.xyz, v);
    return v + r.w * t + cross(r.xyz, t);
}

float4 QuatMul(float4 a, float4 b)
{
    return float4(a.wwww * b + (a.xyzx * b.wwwx + a.yzxy * b.zxyy) * float4(1,1,1,-1) - a.zxyz * b.yzxz);
}

float4 QuatInverse(float4 q)
{
    return rcp(dot(q, q)) * q * float4(-1,-1,-1,1);
}

float3x3 CalcMatrixFromRotationScale(float4 rot, float3 scale)
{
    float3x3 ms = float3x3(
        scale.x, 0, 0,
        0, scale.y, 0,
        0, 0, scale.z
    );
    float x = rot.x;
    float y = rot.y;
    float z = rot.z;
    float w = rot.w;
    float3x3 mr = float3x3(
        1-2*(y*y + z*z),   2*(x*y - w*z),   2*(x*z + w*y),
          2*(x*y + w*z), 1-2*(x*x + z*z),   2*(y*z - w*x),
          2*(x*z - w*y),   2*(y*z + w*x), 1-2*(x*x + y*y)
    );
    return mul(mr, ms);
}

void CalcCovariance3D(float3x3 rotMat, out float3 sigma0, out float3 sigma1)
{
    float3x3 sig = mul(rotMat, transpose(rotMat));
    sigma0 = float3(sig._m00, sig._m01, sig._m02);
    sigma1 = float3(sig._m11, sig._m12, sig._m22);
}

// from "EWA Splatting" (Zwicker et al 2002) eq. 31
// alphaScale returns the Mip-Splatting style opacity compensation for the
// low-pass dilation below: sqrt(det(cov) / det(cov + 0.3*I)). Multiply it
// into splat alpha so sub-pixel splats fade out instead of turning into
// full-opacity 1px noise (aliasing shimmer / popping).
float3 CalcCovariance2D(float3 worldPos, float3 cov3d0, float3 cov3d1, float4x4 matrixV, float4x4 matrixP, float4 screenParams, out float alphaScale)
{
    float4x4 viewMatrix = matrixV;
    float3 viewPos = mul(viewMatrix, float4(worldPos, 1)).xyz;

    // Clamp view-space positions for splats near frustum edges to avoid
    // extreme covariance values. Use abs() because Metal render-to-texture
    // Y-flip makes m11 negative.
    float absM00 = abs(matrixP._m00);
    float absM11 = abs(matrixP._m11);
    float tanFovX = rcp(absM00);
    float tanFovY = rcp(absM11);
    float limX = 1.3 * tanFovX;
    float limY = 1.3 * tanFovY;
    viewPos.x = clamp(viewPos.x / viewPos.z, -limX, limX) * viewPos.z;
    viewPos.y = clamp(viewPos.y / viewPos.z, -limY, limY) * viewPos.z;

    // Separate focal lengths per axis — critical for asymmetric projections
    // (e.g. Vision Pro where focalX != focalY). Original CUDA reference uses
    // separate focal_x / focal_y in the Jacobian.
    float focalX = screenParams.x * absM00 / 2;
    float focalY = screenParams.y * absM11 / 2;

    float3x3 J = float3x3(
        focalX / viewPos.z, 0, -(focalX * viewPos.x) / (viewPos.z * viewPos.z),
        0, focalY / viewPos.z, -(focalY * viewPos.y) / (viewPos.z * viewPos.z),
        0, 0, 0
    );
    float3x3 W = (float3x3)viewMatrix;
    float3x3 T = mul(J, W);
    float3x3 V = float3x3(
        cov3d0.x, cov3d0.y, cov3d0.z,
        cov3d0.y, cov3d1.x, cov3d1.y,
        cov3d0.z, cov3d1.y, cov3d1.z
    );
    float3x3 cov = mul(T, mul(V, transpose(T)));

    // Low pass filter to make each splat at least 1px size.
    float detOrig = cov._m00 * cov._m11 - cov._m01 * cov._m01;
    cov._m00 += 0.3;
    cov._m11 += 0.3;
    float detDilated = cov._m00 * cov._m11 - cov._m01 * cov._m01;
    alphaScale = sqrt(max(detOrig, 0.0) / max(detDilated, 1e-6));
    return float3(cov._m00, cov._m01, cov._m11);
}

float3 CalcConic(float3 cov2d)
{
    float det = cov2d.x * cov2d.z - cov2d.y * cov2d.y;
    return float3(cov2d.z, -cov2d.y, cov2d.x) * rcp(det);
}

float2 CalcScreenSpaceDelta(float2 svPositionXY, float2 centerXY, float4 projectionParams)
{
    float2 d = svPositionXY - centerXY;
    d.y *= projectionParams.x;
    return d;
}

float CalcPowerFromConic(float3 conic, float2 d)
{
    return -0.5 * (conic.x * d.x*d.x + conic.z * d.y*d.y) + conic.y * d.x*d.y;
}

// Morton interleaving 16x16 group i.e. by 4 bits of coordinates, based on this thread:
// https://twitter.com/rygorous/status/986715358852608000
// which is simplified version of https://fgiesen.wordpress.com/2009/12/13/decoding-morton-codes/
uint EncodeMorton2D_16x16(uint2 c)
{
    uint t = ((c.y & 0xF) << 8) | (c.x & 0xF); // ----EFGH----ABCD
    t = (t ^ (t << 2)) & 0x3333;               // --EF--GH--AB--CD
    t = (t ^ (t << 1)) & 0x5555;               // -E-F-G-H-A-B-C-D
    return (t | (t >> 7)) & 0xFF;              // --------EAFBGCHD
}
uint2 DecodeMorton2D_16x16(uint t)      // --------EAFBGCHD
{
    t = (t & 0xFF) | ((t & 0xFE) << 7); // -EAFBGCHEAFBGCHD
    t &= 0x5555;                        // -E-F-G-H-A-B-C-D
    t = (t ^ (t >> 1)) & 0x3333;        // --EF--GH--AB--CD
    t = (t ^ (t >> 2)) & 0x0f0f;        // ----EFGH----ABCD
    return uint2(t & 0xF, t >> 8);      // --------EFGHABCD
}


static const half SH_C1 = 0.4886025;
static const half SH_C2[] = { 1.0925484, -1.0925484, 0.3153916, -1.0925484, 0.5462742 };
static const half SH_C3[] = { -0.5900436, 2.8906114, -0.4570458, 0.3731763, -0.4570458, 1.4453057, -0.5900436 };

struct SplatSHData
{
    half3 col, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14, sh15;
};

half3 ShadeSH(SplatSHData splat, half3 dir, int shOrder, bool onlySH)
{
    dir *= -1;

    half x = dir.x, y = dir.y, z = dir.z;

    // ambient band
    half3 res = splat.col; // col = sh0 * SH_C0 + 0.5 is already precomputed
    if (onlySH)
        res = 0.5;
    // 1st degree
    if (shOrder >= 1)
    {
        res += SH_C1 * (-splat.sh1 * y + splat.sh2 * z - splat.sh3 * x);
        // 2nd degree
        if (shOrder >= 2)
        {
            half xx = x * x, yy = y * y, zz = z * z;
            half xy = x * y, yz = y * z, xz = x * z;
            res +=
                (SH_C2[0] * xy) * splat.sh4 +
                (SH_C2[1] * yz) * splat.sh5 +
                (SH_C2[2] * (2 * zz - xx - yy)) * splat.sh6 +
                (SH_C2[3] * xz) * splat.sh7 +
                (SH_C2[4] * (xx - yy)) * splat.sh8;
            // 3rd degree
            if (shOrder >= 3)
            {
                res +=
                    (SH_C3[0] * y * (3 * xx - yy)) * splat.sh9 +
                    (SH_C3[1] * xy * z) * splat.sh10 +
                    (SH_C3[2] * y * (4 * zz - xx - yy)) * splat.sh11 +
                    (SH_C3[3] * z * (2 * zz - 3 * xx - 3 * yy)) * splat.sh12 +
                    (SH_C3[4] * x * (4 * zz - xx - yy)) * splat.sh13 +
                    (SH_C3[5] * z * (xx - yy)) * splat.sh14 +
                    (SH_C3[6] * x * (xx - 3 * yy)) * splat.sh15;
            }
        }
    }
    return max(res, 0);
}

static const uint kTexWidth = 2048;

uint3 SplatIndexToPixelIndex(uint idx)
{
    uint3 res;

    uint2 xy = DecodeMorton2D_16x16(idx);
    uint width = kTexWidth / 16;
    idx >>= 8;
    res.x = (idx % width) * 16 + xy.x;
    res.y = (idx / width) * 16 + xy.y;
    res.z = 0;
    return res;
}

struct SplatChunkInfo
{
    uint colR, colG, colB, colA;
    float2 posX, posY, posZ;
    uint sclX, sclY, sclZ;
    uint shR, shG, shB;
};

#ifndef GAUSSIAN_SPLAT_SKIP_BUFFER_DECLARATIONS
StructuredBuffer<SplatChunkInfo> _SplatChunks;
uint _SplatChunkCount;
#endif

static const uint kChunkSize = 256;

struct SplatData
{
    float3 pos;
    float4 rot;
    float3 scale;
    half opacity;
    SplatSHData sh;
};

// Decode quaternion from a "smallest 3" e.g. 10.10.10.2 format
float4 DecodeRotation(float4 pq)
{
    uint idx = (uint)round(pq.w * 3.0); // note: need to round or index might come out wrong in some formats (e.g. fp16.fp16.fp16.fp16)
    float4 q;
    q.xyz = pq.xyz * sqrt(2.0) - (1.0 / sqrt(2.0));
    q.w = sqrt(1.0 - saturate(dot(q.xyz, q.xyz)));
    if (idx == 0) q = q.wxyz;
    if (idx == 1) q = q.xwyz;
    if (idx == 2) q = q.xywz;
    return q;
}
float4 PackSmallest3Rotation(float4 q)
{
    // find biggest component
    float4 absQ = abs(q);
    int index = 0;
    float maxV = absQ.x;
    if (absQ.y > maxV)
    {
        index = 1;
        maxV = absQ.y;
    }
    if (absQ.z > maxV)
    {
        index = 2;
        maxV = absQ.z;
    }
    if (absQ.w > maxV)
    {
        index = 3;
        maxV = absQ.w;
    }

    if (index == 0) q = q.yzwx;
    if (index == 1) q = q.xzwy;
    if (index == 2) q = q.xywz;

    float3 three = q.xyz * (q.w >= 0 ? 1 : -1); // -1/sqrt2..+1/sqrt2 range
    three = (three * sqrt(2.0)) * 0.5 + 0.5; // 0..1 range
    return float4(three, index / 3.0);
}

half3 DecodePacked_6_5_5(uint enc)
{
    return half3(
        (enc & 63) / 63.0,
        ((enc >> 6) & 31) / 31.0,
        ((enc >> 11) & 31) / 31.0);
}

half3 DecodePacked_5_6_5(uint enc)
{
    return half3(
        (enc & 31) / 31.0,
        ((enc >> 5) & 63) / 63.0,
        ((enc >> 11) & 31) / 31.0);
}

half3 DecodePacked_11_10_11(uint enc)
{
    return half3(
        (enc & 2047) / 2047.0,
        ((enc >> 11) & 1023) / 1023.0,
        ((enc >> 21) & 2047) / 2047.0);
}

float3 DecodePacked_16_16_16(uint2 enc)
{
    return float3(
        (enc.x & 65535) / 65535.0,
        ((enc.x >> 16) & 65535) / 65535.0,
        (enc.y & 65535) / 65535.0);
}

float4 DecodePacked_10_10_10_2(uint enc)
{
    return float4(
        (enc & 1023) / 1023.0,
        ((enc >> 10) & 1023) / 1023.0,
        ((enc >> 20) & 1023) / 1023.0,
        ((enc >> 30) & 3) / 3.0);
}
uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
{
    return (uint) (v.x * 1023.5f) | ((uint) (v.y * 1023.5f) << 10) | ((uint) (v.z * 1023.5f) << 20) | ((uint) (v.w * 3.5f) << 30);
}


#ifndef GAUSSIAN_SPLAT_SKIP_BUFFER_DECLARATIONS
#ifdef SHADER_STAGE_COMPUTE
#define SplatBufferDataType RWByteAddressBuffer
#else
#define SplatBufferDataType ByteAddressBuffer
#endif

SplatBufferDataType _SplatPos;
SplatBufferDataType _SplatOther;
SplatBufferDataType _SplatSH;
Texture2D _SplatColor;
uint _SplatFormat;
// Match GaussianSplatAsset.VectorFormat
#define VECTOR_FMT_32F 0
#define VECTOR_FMT_16 1
#define VECTOR_FMT_11 2
#define VECTOR_FMT_6 3

uint LoadUShort(SplatBufferDataType dataBuffer, uint addrU)
{
    uint addrA = addrU & ~0x3;
    uint val = dataBuffer.Load(addrA);
    if (addrU != addrA)
        val >>= 16;
    return val & 0xFFFF;
}

uint LoadUInt(SplatBufferDataType dataBuffer, uint addrU)
{
    uint addrA = addrU & ~0x3;
    uint val = dataBuffer.Load(addrA);
    if (addrU != addrA)
    {
        uint val1 = dataBuffer.Load(addrA + 4);
        val = (val >> 16) | ((val1 & 0xFFFF) << 16);
    }
    return val;
}

float3 LoadAndDecodeVector(SplatBufferDataType dataBuffer, uint addrU, uint fmt)
{
    uint addrA = addrU & ~0x3;

    uint val0 = dataBuffer.Load(addrA);

    float3 res = 0;
    if (fmt == VECTOR_FMT_32F)
    {
        uint val1 = dataBuffer.Load(addrA + 4);
        uint val2 = dataBuffer.Load(addrA + 8);
        if (addrU != addrA)
        {
            uint val3 = dataBuffer.Load(addrA + 12);
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
            val1 = (val1 >> 16) | ((val2 & 0xFFFF) << 16);
            val2 = (val2 >> 16) | ((val3 & 0xFFFF) << 16);
        }
        res = float3(asfloat(val0), asfloat(val1), asfloat(val2));
    }
    else if (fmt == VECTOR_FMT_16)
    {
        uint val1 = dataBuffer.Load(addrA + 4);
        if (addrU != addrA)
        {
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
            val1 >>= 16;
        }
        res = DecodePacked_16_16_16(uint2(val0, val1));
    }
    else if (fmt == VECTOR_FMT_11)
    {
        uint val1 = dataBuffer.Load(addrA + 4);
        if (addrU != addrA)
        {
            val0 = (val0 >> 16) | ((val1 & 0xFFFF) << 16);
        }
        res = DecodePacked_11_10_11(val0);
    }
    else if (fmt == VECTOR_FMT_6)
    {
        if (addrU != addrA)
            val0 >>= 16;
        res = DecodePacked_6_5_5(val0);
    }
    return res;
}

float3 LoadSplatPosValue(uint index)
{
    uint fmt = SplatFormatPos(_SplatFormat);
    uint stride = 0;
    if (fmt == VECTOR_FMT_32F)
        stride = 12;
    else if (fmt == VECTOR_FMT_16)
        stride = 6;
    else if (fmt == VECTOR_FMT_11)
        stride = 4;
    else if (fmt == VECTOR_FMT_6)
        stride = 2;
    return LoadAndDecodeVector(_SplatPos, index * stride, fmt);
}

float3 LoadSplatPos(uint idx)
{
    float3 pos = LoadSplatPosValue(idx);
    uint chunkIdx = idx / kChunkSize;
    if (chunkIdx < _SplatChunkCount)
    {
        SplatChunkInfo chunk = _SplatChunks[chunkIdx];
        float3 posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
        float3 posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
        pos = lerp(posMin, posMax, pos);
    }
    return pos;
}

half4 LoadSplatColTex(uint3 coord)
{
    return _SplatColor.Load(coord);
}

// SH coefficient k of the entry at addr, laid out as GaussianSplatAsset.GetSHStride describes
half3 LoadSplatSHCoeff(uint addr, uint shFormat, uint k)
{
    if (shFormat == SPLAT_SH_FMT_32F)
        return asfloat(_SplatSH.Load3(addr + k * 12));
    if (shFormat == SPLAT_SH_FMT_11)
        return DecodePacked_11_10_11(_SplatSH.Load(addr + k * 4));
    if (shFormat == SPLAT_SH_FMT_6)
        return DecodePacked_5_6_5(_SplatSH.Load(addr + (k >> 1) * 4) >> ((k & 1) * 16));
    // Float16 and the cluster tables: 6 bytes each, so odd ones start mid-word
    uint2 raw = _SplatSH.Load2(addr + ((k * 6) & ~3));
    if (k & 1)
        raw = uint2((raw.x >> 16) | (raw.y << 16), raw.y >> 16);
    return half3(f16tof32(raw.x), f16tof32(raw.x >> 16), f16tof32(raw.y));
}

// Reads SH bands up to maxSHOrder (capped by what the asset stores) and
// leaves the rest zero; 0 skips every SH read, which is all an SH order 0
// render needs, at a fraction of the bandwidth.
SplatData LoadSplatData(uint idx, uint maxSHOrder)
{
    SplatData s = (SplatData)0;

    // figure out raw data offsets / locations
    uint3 coord = SplatIndexToPixelIndex(idx);

    uint scaleFmt = SplatFormatScale(_SplatFormat);
    uint shFormat = SplatFormatSH(_SplatFormat);
    uint shDegree = SplatFormatSHDegree(_SplatFormat);

    uint otherStride = 4; // rotation is 10.10.10.2
    if (scaleFmt == VECTOR_FMT_32F)
        otherStride += 12;
    else if (scaleFmt == VECTOR_FMT_16)
        otherStride += 6;
    else if (scaleFmt == VECTOR_FMT_11)
        otherStride += 4;
    else if (scaleFmt == VECTOR_FMT_6)
        otherStride += 2;
    if (SplatSHClustered(shFormat))
        otherStride += 2;
    uint otherAddr = idx * otherStride;

    uint chunkIdx = idx / kChunkSize;
    bool chunked = chunkIdx < _SplatChunkCount;
    SplatChunkInfo chunk = (SplatChunkInfo)0;
    if (chunked)
        chunk = _SplatChunks[chunkIdx];

    // load raw splat data, which might be chunk-relative
    s.pos       = LoadSplatPosValue(idx);
    s.rot       = DecodeRotation(DecodePacked_10_10_10_2(LoadUInt(_SplatOther, otherAddr)));
    s.scale     = LoadAndDecodeVector(_SplatOther, otherAddr + 4, scaleFmt);
    half4 col   = LoadSplatColTex(coord);

    uint shCount = SplatSHCoeffCount(min(shDegree, maxSHOrder));
    if (shCount > 0)
    {
        uint shIndex = idx;
        if (SplatSHClustered(shFormat))
            shIndex = LoadUShort(_SplatOther, otherAddr + otherStride - 2);
        uint shAddr = shIndex * SplatSHStride(shFormat, shDegree);

        // Float16/Norm11/Norm6 are chunk-relative; Float32 and the cluster
        // tables are stored as-is, which lerp(0, 1, x) leaves untouched.
        half3 shMin = 0;
        half3 shMax = 1;
        if (chunked && shFormat > SPLAT_SH_FMT_32F && !SplatSHClustered(shFormat))
        {
            shMin = half3(f16tof32(chunk.shR    ), f16tof32(chunk.shG    ), f16tof32(chunk.shB    ));
            shMax = half3(f16tof32(chunk.shR>>16), f16tof32(chunk.shG>>16), f16tof32(chunk.shB>>16));
        }

        if (shCount > 0)  s.sh.sh1  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 0));
        if (shCount > 1)  s.sh.sh2  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 1));
        if (shCount > 2)  s.sh.sh3  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 2));
        if (shCount > 3)  s.sh.sh4  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 3));
        if (shCount > 4)  s.sh.sh5  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 4));
        if (shCount > 5)  s.sh.sh6  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 5));
        if (shCount > 6)  s.sh.sh7  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 6));
        if (shCount > 7)  s.sh.sh8  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 7));
        if (shCount > 8)  s.sh.sh9  = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 8));
        if (shCount > 9)  s.sh.sh10 = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 9));
        if (shCount > 10) s.sh.sh11 = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 10));
        if (shCount > 11) s.sh.sh12 = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 11));
        if (shCount > 12) s.sh.sh13 = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 12));
        if (shCount > 13) s.sh.sh14 = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 13));
        if (shCount > 14) s.sh.sh15 = lerp(shMin, shMax, LoadSplatSHCoeff(shAddr, shFormat, 14));
    }

    // if raw data is chunk-relative, convert to final values by interpolating between chunk min/max
    if (chunked)
    {
        float3 posMin = float3(chunk.posX.x, chunk.posY.x, chunk.posZ.x);
        float3 posMax = float3(chunk.posX.y, chunk.posY.y, chunk.posZ.y);
        half3 sclMin = half3(f16tof32(chunk.sclX    ), f16tof32(chunk.sclY    ), f16tof32(chunk.sclZ    ));
        half3 sclMax = half3(f16tof32(chunk.sclX>>16), f16tof32(chunk.sclY>>16), f16tof32(chunk.sclZ>>16));
        half4 colMin = half4(f16tof32(chunk.colR    ), f16tof32(chunk.colG    ), f16tof32(chunk.colB    ), f16tof32(chunk.colA    ));
        half4 colMax = half4(f16tof32(chunk.colR>>16), f16tof32(chunk.colG>>16), f16tof32(chunk.colB>>16), f16tof32(chunk.colA>>16));
        s.pos = lerp(posMin, posMax, s.pos);
        s.scale     = lerp(sclMin, sclMax, s.scale);
        s.scale *= s.scale;
        s.scale *= s.scale;
        s.scale *= s.scale;
        col   = lerp(colMin, colMax, col);
        col.a = InvSquareCentered01(col.a);
    }
    s.opacity   = col.a;
    s.sh.col    = col.rgb;

    return s;
}

SplatData LoadSplatData(uint idx)
{
    return LoadSplatData(idx, 3);
}

#endif // GAUSSIAN_SPLAT_SKIP_BUFFER_DECLARATIONS

struct SplatViewData
{
    float4 pos;
    float2 axis1, axis2;
    uint2 color; // see PackViewColor / PackBakedViewColor
};

// Shared-exponent 9:9:9:5 (DXGI R9G9B9E5_SHAREDEXP): three non-negative
// values in 32 bits, 9 bits of mantissa relative to the largest
uint PackRGB9E5(float3 rgb)
{
    rgb = clamp(rgb, 0, 65408.0);
    float maxc = max(max(rgb.r, rgb.g), max(rgb.b, exp2(-16.0)));
    int e = (int)floor(log2(maxc)) + 16;
    float scale = exp2((float)(e - 24));
    if (floor(maxc / scale + 0.5) >= 512)
    {
        e++;
        scale *= 2;
    }
    uint3 m = (uint3)min(floor(rgb / scale + 0.5), 511);
    return m.r | (m.g << 9) | (m.b << 18) | ((uint)e << 27);
}

float3 UnpackRGB9E5(uint v)
{
    return float3(v & 511, (v >> 9) & 511, (v >> 18) & 511) * exp2((float)(v >> 27) - 24);
}

// View data color of a plain splat: four halves
uint2 PackViewColor(half4 col)
{
    return uint2((f32tof16(col.r) << 16) | f32tof16(col.g), (f32tof16(col.b) << 16) | f32tof16(col.a));
}

// View data color of a baked quad (RenderGaussianStylized.shader), which
// also carries the splat's style: the quad shader morphs each quad's
// profile by its weight into its slot's shape (GaussianStyleSet). Color as
// shared-exponent 9:9:9:5; the slot in the top 2 bits of .y, the weight as
// 14-bit unorm below it, the alpha as a half below that.
uint2 PackBakedViewColor(half3 rgb, half alpha, float styleWeight, uint styleSlot)
{
    uint w = (uint)round(saturate(styleWeight) * 16383.0);
    return uint2(PackRGB9E5(rgb), (styleSlot << 30) | (w << 16) | f32tof16(alpha));
}

float BakedViewStyleWeight(uint2 color)
{
    return ((color.y >> 16) & 0x3FFF) * (1.0 / 16383.0);
}

uint BakedViewStyleSlot(uint2 color)
{
    return color.y >> 30;
}

// Alpha is the low half of .y in both layouts
half ViewAlpha(uint2 color)
{
    return f16tof32(color.y);
}

void SetViewAlpha(inout uint2 color, half alpha)
{
    color.y = (color.y & 0xFFFF0000) | f32tof16(alpha);
}

// What a splat or fragment changes in the image, for the opacity culls: the
// light it blocks (alpha) or the light it adds (alpha * colour), whichever is
// more. Colours up to white leave just alpha; a style driving colour past
// white keeps what is too faint to occlude but bright enough to see.
float SplatContribution(float alpha, float3 rgb)
{
    return alpha * max(1.0, max(rgb.r, max(rgb.g, rgb.b)));
}

// If we are rendering into backbuffer directly (e.g. HDR off, no postprocessing),
// the color target texture is a render target (so projection is upside down),
// but the depth buffer we get is not upside down. We want to flip
// our rendering upside down manually for this case.
//
// There does not seem to be a good way to detect this situation in Unity; work around it
// by setting _CameraTargetTexture global texture to BuiltinRenderTextureType.CameraTarget
// from the command buffer. When CameraTarget will be null (i.e. backbuffer), the _TexeSize
// property of the texture will get set to (1,1,1,1).
//
// One could hope someday Unity will fix all this upside-down thingy...
float4 _CameraTargetTexture_TexelSize;
void FlipProjectionIfBackbuffer(inout float4 vpos)
{
    if (_CameraTargetTexture_TexelSize.z == 1.0)
        vpos.y = -vpos.y;
}

// Foveated rendering (VRR) coordinate remap for visionOS Metal.
// Custom wrapper that avoids SRP Core dependencies (PositionInputs etc.)
// while using the same hlslcc VRR tokens Unity uses internally.
#if defined(SUPPORTS_FOVEATED_RENDERING_NON_UNIFORM_RASTER) && defined(SHADER_API_METAL)

float2 _UV_HlslccVRRDistort;
float2 _UV_HlslccVRRResolve;

// Non-uniform (VRR physical) UV → linear UV
float2 GaussianRemapNonUniformToLinear(float2 uv, uint eyeIndex)
{
    UNITY_BRANCH if (_FOVEATED_RENDERING_NON_UNIFORM_RASTER)
    {
        uv.y = 1.0 - uv.y;
        uv = mad(uv, _UV_HlslccVRRDistort, eyeIndex);
        uv.y = 1.0 - uv.y;
    }
    return uv;
}

// Linear UV → non-uniform (VRR physical) UV
float2 GaussianRemapLinearToNonUniform(float2 uv, uint eyeIndex)
{
    UNITY_BRANCH if (_FOVEATED_RENDERING_NON_UNIFORM_RASTER)
    {
        uv.y = 1.0 - uv.y;
        uv = mad(uv, _UV_HlslccVRRResolve, eyeIndex);
        uv.y = 1.0 - uv.y;
    }
    return uv;
}

#else

float2 GaussianRemapNonUniformToLinear(float2 uv, uint eyeIndex) { return uv; }
float2 GaussianRemapLinearToNonUniform(float2 uv, uint eyeIndex) { return uv; }

#endif

#endif // GAUSSIAN_SPLATTING_HLSL
