// SPDX-License-Identifier: MIT
// Influence of a GaussianVolume, shared by the passes that weight splats by
// one (animation modifiers, style masks).
#ifndef GAUSSIAN_VOLUME_HLSL
#define GAUSSIAN_VOLUME_HLSL

// 1 inside the volume, easing to 0 over the falloff distance outside it.
// localPos is in the volume's local space, where the shape (GaussianVolumeShape:
// 0 = box, 1 = sphere, 2 = unbounded) is the unit cube, the unit-diameter
// sphere or everywhere; the falloff is in the same units.
float GaussianVolumeWeight(float3 localPos, float shape, float falloff)
{
    if (shape > 1.5)
        return 1;
    falloff = max(falloff, 0.001);

    // Signed distance to the surface: negative inside, positive outside
    float dist;
    if (shape < 0.5)
    {
        float3 d = abs(localPos) - 0.5;
        float inside = max(max(d.x, d.y), d.z);
        dist = inside < 0 ? inside : length(max(d, 0));
    }
    else
    {
        dist = length(localPos) - 0.5;
    }

    if (dist > falloff)
        return 0;
    if (dist < 0)
        return 1;
    float t = dist / falloff;
    return 1.0 - t * t;
}

#endif // GAUSSIAN_VOLUME_HLSL
