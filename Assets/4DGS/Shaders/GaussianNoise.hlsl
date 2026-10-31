// SPDX-License-Identifier: MIT
// Hash and value noise shared by the animation and particle kernels.
#ifndef GAUSSIAN_NOISE_HLSL
#define GAUSSIAN_NOISE_HLSL

float Hash31(float3 p)
{
    p = frac(p * float3(443.8975, 397.2973, 491.1871));
    p += dot(p, p.zxy + 19.19);
    return frac(p.x * p.y * p.z);
}

float3 Hash33(float3 p)
{
    return float3(
        Hash31(p),
        Hash31(p + 127.1),
        Hash31(p + 269.5)
    );
}

// Simple value noise
float ValueNoise3D(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    f = f * f * (3.0 - 2.0 * f); // smoothstep

    float a = Hash31(i);
    float b = Hash31(i + float3(1, 0, 0));
    float c = Hash31(i + float3(0, 1, 0));
    float d = Hash31(i + float3(1, 1, 0));
    float e = Hash31(i + float3(0, 0, 1));
    float ff = Hash31(i + float3(1, 0, 1));
    float g = Hash31(i + float3(0, 1, 1));
    float h = Hash31(i + float3(1, 1, 1));

    return lerp(
        lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y),
        lerp(lerp(e, ff, f.x), lerp(g, h, f.x), f.y),
        f.z);
}

// The same value noise with its analytic gradient: (value, d/dx, d/dy, d/dz)
float4 ValueNoise3DGrad(float3 p)
{
    float3 i = floor(p);
    float3 f = frac(p);
    float3 u = f * f * (3.0 - 2.0 * f);
    float3 du = 6.0 * f * (1.0 - f);

    float a = Hash31(i);
    float b = Hash31(i + float3(1, 0, 0));
    float c = Hash31(i + float3(0, 1, 0));
    float d = Hash31(i + float3(1, 1, 0));
    float e = Hash31(i + float3(0, 0, 1));
    float ff = Hash31(i + float3(1, 0, 1));
    float g = Hash31(i + float3(0, 1, 1));
    float h = Hash31(i + float3(1, 1, 1));

    float k1 = b - a;
    float k2 = c - a;
    float k3 = e - a;
    float k4 = a - b - c + d;
    float k5 = a - c - e + g;
    float k6 = a - b - e + ff;
    float k7 = -a + b + c - d + e - ff - g + h;

    float value = a + k1 * u.x + k2 * u.y + k3 * u.z + k4 * u.x * u.y + k5 * u.y * u.z + k6 * u.z * u.x + k7 * u.x * u.y * u.z;
    float3 grad = du * float3(
        k1 + k4 * u.y + k6 * u.z + k7 * u.y * u.z,
        k2 + k5 * u.z + k4 * u.x + k7 * u.z * u.x,
        k3 + k6 * u.x + k5 * u.y + k7 * u.x * u.y);
    return float4(value, grad);
}

#endif // GAUSSIAN_NOISE_HLSL
