// SPDX-License-Identifier: MIT
// Quantize block (GaussianStyleQuantizeBlock): snaps the splats onto slices,
// a grid or a lattice.
#ifndef GAUSSIAN_STYLE_QUANTIZE_HLSL
#define GAUSSIAN_STYLE_QUANTIZE_HLSL

// GaussianSplatStyleQuantize
#define STYLE_QUANTIZE_SLICES 1
#define STYLE_QUANTIZE_GRID 2
#define STYLE_QUANTIZE_LATTICE 3

struct StyleQuantize
{
    bool on;
    uint mode;              // STYLE_QUANTIZE_*
    float spacing;          // world units
    float band;             // world units around a slice, grid point or lattice plane that stay
    float3 sliceNormal;     // world, normalized
    float sliceOffset;
    bool matte;             // splats outside the band stay, black
};
#define STYLE_QUANTIZE_ROWS 3
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
    b.matte = _StyleQuantize[slot * STYLE_QUANTIZE_ROWS + 2].x > 0.5;
    return b;
}

// Where the block snaps a splat to, and how far that is from where it is
float3 StyleQuantizeSnap(StyleQuantize b, float3 worldPos, out float dist)
{
    if (b.mode == STYLE_QUANTIZE_SLICES)
    {
        float d = (dot(worldPos, b.sliceNormal) - b.sliceOffset) / b.spacing;
        float offset = (round(d) - d) * b.spacing;
        dist = abs(offset);
        return worldPos + b.sliceNormal * offset;
    }
    if (b.mode == STYLE_QUANTIZE_GRID)
    {
        float3 snapped = round(worldPos / b.spacing) * b.spacing;
        float3 offset = abs(snapped - worldPos);
        dist = max(offset.x, max(offset.y, offset.z));
        return snapped;
    }
    if (b.mode == STYLE_QUANTIZE_LATTICE)
    {
        // Only along the axis whose plane is nearest, so the splat stays
        // on its surface and lands on the line that plane cuts into it
        float3 offset = round(worldPos / b.spacing) * b.spacing - worldPos;
        float3 away = abs(offset);
        dist = min(away.x, min(away.y, away.z));
        float3 nearest = away.x <= dist ? float3(1, 0, 0) : (away.y <= dist ? float3(0, 1, 0) : float3(0, 0, 1));
        return worldPos + offset * nearest;
    }
    dist = 0;
    return worldPos;
}

// How much of a splat this far from its slice, grid point or lattice plane
// stays. A soft edge, so splats drifting across it (animation) fade rather
// than pop
float StyleQuantizeBand(StyleQuantize b, float dist)
{
    return 1.0 - smoothstep(b.band * 0.7, b.band, dist);
}

// Snaps the splat to its slice, grid point or lattice plane and fades the
// ones too far from one, so at full weight the opacity cull removes them
// from sorting and drawing. Under matte those stay in place instead, for
// the mask to darken.
void StyleQuantizeDisplace(StyleContext c, inout float3 worldPos, inout float keep)
{
    StyleQuantize b = LoadStyleQuantize(c.slot);
    if (!b.on)
        return;
    float dist;
    float3 snapped = StyleQuantizeSnap(b, worldPos, dist);
    if (!b.matte)
    {
        keep *= StyleQuantizeBand(b, dist);
        worldPos = snapped;
    }
    else if (dist < b.band)
    {
        worldPos = snapped;
    }
}

// Under matte, black for the splats outside the band. A snapped splat is
// on its plane wherever this is asked from, before the move or after.
void StyleQuantizeMask(StyleContext c, inout float mask)
{
    StyleQuantize b = LoadStyleQuantize(c.slot);
    if (!b.on || !b.matte)
        return;
    float dist;
    StyleQuantizeSnap(b, c.worldPos, dist);
    mask *= StyleQuantizeBand(b, dist);
}

#endif // GAUSSIAN_STYLE_QUANTIZE_HLSL
