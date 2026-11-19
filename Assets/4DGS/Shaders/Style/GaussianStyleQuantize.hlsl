// SPDX-License-Identifier: MIT
// Quantize block (GaussianStyleQuantizeBlock): snaps the splats onto slices
// or a grid.
#ifndef GAUSSIAN_STYLE_QUANTIZE_HLSL
#define GAUSSIAN_STYLE_QUANTIZE_HLSL

// GaussianSplatStyleQuantize
#define STYLE_QUANTIZE_SLICES 1
#define STYLE_QUANTIZE_GRID 2

struct StyleQuantize
{
    bool on;
    uint mode;              // STYLE_QUANTIZE_*
    float spacing;          // world units
    float band;             // world units around a slice or grid point that stay
    float3 sliceNormal;     // world, normalized
    float sliceOffset;
};
#define STYLE_QUANTIZE_ROWS 2
float4 _StyleQuantize[MAX_STYLES * STYLE_QUANTIZE_ROWS];

// Mirrors GaussianStyleQuantizeBlock.Data
StyleQuantize LoadStyleQuantize(uint slot)
{
    float4 r0 = _StyleQuantize[slot * STYLE_QUANTIZE_ROWS];
    float4 r1 = _StyleQuantize[slot * STYLE_QUANTIZE_ROWS + 1];
    StyleQuantize b;
    b.on = r0.x > 0.5;
    b.mode = (uint)r0.y;
    b.spacing = r0.z;
    b.band = r0.w;
    b.sliceNormal = r1.xyz;
    b.sliceOffset = r1.w;
    return b;
}

// Snaps the splat to its slice or grid point and fades the ones too far
// from one, so at full weight the opacity cull removes them from sorting
// and drawing
void StyleQuantizeDisplace(StyleContext c, inout float3 worldPos, inout float keep)
{
    StyleQuantize b = LoadStyleQuantize(c.slot);
    if (!b.on)
        return;
    float3 snapped = worldPos;
    float dist = 0;
    if (b.mode == STYLE_QUANTIZE_SLICES)
    {
        float d = (dot(worldPos, b.sliceNormal) - b.sliceOffset) / b.spacing;
        float offset = (round(d) - d) * b.spacing;
        snapped = worldPos + b.sliceNormal * offset;
        dist = abs(offset);
    }
    else if (b.mode == STYLE_QUANTIZE_GRID)
    {
        snapped = round(worldPos / b.spacing) * b.spacing;
        float3 offset = abs(snapped - worldPos);
        dist = max(offset.x, max(offset.y, offset.z));
    }
    // A soft edge, so splats drifting across it (animation) fade rather than pop
    keep *= 1.0 - smoothstep(b.band * 0.7, b.band, dist);
    worldPos = snapped;
}

#endif // GAUSSIAN_STYLE_QUANTIZE_HLSL
