// SPDX-License-Identifier: MIT
// Glint block (GaussianStyleGlintBlock): a fraction of the splats flash
// briefly, each on its own clock so the flashes never line up.
#ifndef GAUSSIAN_STYLE_GLINT_HLSL
#define GAUSSIAN_STYLE_GLINT_HLSL

struct StyleGlint
{
    bool on;
    float density;          // fraction of splats that flash
    float rate;             // flashes per second
    float intensity;        // brightness added at the peak
};
#define STYLE_GLINT_ROWS 1
float4 _StyleGlint[MAX_STYLES * STYLE_GLINT_ROWS];

// Mirrors GaussianStyleGlintBlock.Data
StyleGlint LoadStyleGlint(uint slot)
{
    float4 r0 = _StyleGlint[slot * STYLE_GLINT_ROWS];
    StyleGlint b;
    b.on = r0.x > 0.5;
    b.density = r0.y;
    b.rate = r0.z;
    b.intensity = r0.w;
    return b;
}

void StyleGlintLight(StyleContext c, inout float gain, inout float3 emission)
{
    StyleGlint b = LoadStyleGlint(c.slot);
    if (!b.on || b.density <= 0 || StyleHash01(c.idx) >= b.density)
        return;
    float h = StyleHash01(c.idx ^ 0x68e31da4u);
    float phase = frac(_StyleTime * b.rate * (0.5 + h) + h * 7.31);
    // A peak a sixth of the period wide, centred on half phase
    float flash = saturate(1.0 - abs(phase * 2.0 - 1.0) * 6.0);
    gain *= 1.0 + flash * flash * b.intensity;
}

void StyleGlintBound(uint slot, inout float gain, inout float emission)
{
    StyleGlint b = LoadStyleGlint(slot);
    if (!b.on || b.density <= 0)
        return;
    gain *= 1.0 + b.intensity;
}

#endif // GAUSSIAN_STYLE_GLINT_HLSL
