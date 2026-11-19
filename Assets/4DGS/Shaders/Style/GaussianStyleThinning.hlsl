// SPDX-License-Identifier: MIT
// Thinning block (GaussianStyleThinningBlock): drops a share of the splats.
#ifndef GAUSSIAN_STYLE_THINNING_HLSL
#define GAUSSIAN_STYLE_THINNING_HLSL

struct StyleThinning
{
    bool on;
    float keepFraction;
    float densityDistance;  // camera distance the density is tuned at (world units, 0 = off)
};
#define STYLE_THINNING_ROWS 1
float4 _StyleThinning[MAX_STYLES * STYLE_THINNING_ROWS];

// Mirrors GaussianStyleThinningBlock.Data
StyleThinning LoadStyleThinning(uint slot)
{
    float4 r0 = _StyleThinning[slot * STYLE_THINNING_ROWS];
    StyleThinning b;
    b.on = r0.x > 0.5;
    b.keepFraction = r0.y;
    b.densityDistance = r0.z;
    return b;
}

// A splat's random rank against the kept fraction, which past the density
// distance shrinks with the square of the camera distance, so dots of a
// fixed pixel size keep their density on screen. The same splats go first
// at any distance, and those near the cut fade instead of popping as the
// camera moves. From the camera centre, so both eyes agree.
void StyleThinningDisplace(StyleContext c, inout float3 worldPos, inout float keep)
{
    StyleThinning b = LoadStyleThinning(c.slot);
    if (!b.on)
        return;
    float fraction = b.keepFraction;
    if (b.densityDistance > 0)
    {
        float ratio = b.densityDistance / max(distance(worldPos, _VecWorldSpaceCameraPos.xyz), 1e-4);
        fraction *= min(ratio * ratio, 1.0);
    }
    if (fraction >= 1)
        return;
    // A band a tenth of the kept fraction wide, centred on the cut so the
    // kept light matches the fraction
    float band = max(fraction * 0.1, 1e-5);
    keep *= saturate((fraction - StyleHash01(c.idx ^ 0x2c1b3c6du)) / band + 0.5);
}

#endif // GAUSSIAN_STYLE_THINNING_HLSL
