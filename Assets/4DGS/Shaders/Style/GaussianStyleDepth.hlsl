// SPDX-License-Identifier: MIT
// Depth block (GaussianStyleDepthBlock): a mask that is white on the side of
// its reference facing the camera and black behind it.
#ifndef GAUSSIAN_STYLE_DEPTH_HLSL
#define GAUSSIAN_STYLE_DEPTH_HLSL

// GaussianSplatStyleDepthReference
#define STYLE_DEPTH_VOLUME_CENTRE 0
#define STYLE_DEPTH_MESH 1

struct StyleDepth
{
    bool on;
    float amount;           // how far the mask darkens
    float inverseRange;     // reciprocal of the depth the mask spans
    float offset;           // moves its middle away from the camera
    uint reference;         // STYLE_DEPTH_*
};
#define STYLE_DEPTH_ROWS 2
float4 _StyleDepth[MAX_STYLES * STYLE_DEPTH_ROWS];

// Mirrors GaussianStyleDepthBlock.Data
StyleDepth LoadStyleDepth(uint slot)
{
    float4 r0 = _StyleDepth[slot * STYLE_DEPTH_ROWS];
    float4 r1 = _StyleDepth[slot * STYLE_DEPTH_ROWS + 1];
    StyleDepth b;
    b.on = r0.x > 0.5;
    b.amount = r0.y;
    b.inverseRange = r0.z;
    b.offset = r0.w;
    b.reference = (uint)r1.x;
    return b;
}

// The mesh each splat sits on (GaussianMeshDriver): its index, 16 bits per
// splat, two to a uint with the first in the low half; and each mesh's
// centre as posed, in the splats' object space
StructuredBuffer<uint> _SplatMesh;
StructuredBuffer<float4> _SplatMeshCenters;
uint _SplatMeshValid;
float4x4 _SplatMeshObjectToWorld;

// World position a splat's depth is measured against: its style volume's
// centre, or the centre of its own mesh where a mesh driver gives one
float3 StyleDepthReference(StyleContext c)
{
    if (LoadStyleDepth(c.slot).reference == STYLE_DEPTH_MESH && _SplatMeshValid != 0)
    {
        uint mesh = (_SplatMesh[c.idx >> 1] >> ((c.idx & 1) * 16)) & 0xFFFFu;
        return mul(_SplatMeshObjectToWorld, float4(_SplatMeshCenters[mesh].xyz, 1)).xyz;
    }
    return LoadStyleVolume(c.volume).center;
}

// The mask: 1 nearer the camera than the reference by half the range or
// more, 0 that far behind it, and 1 without the block. Distances from the
// camera centre, so it does not change as the view turns. The color block's
// Volume Depth ramp input reads it too.
float StyleDepthTerm(StyleContext c)
{
    StyleDepth b = LoadStyleDepth(c.slot);
    if (!b.on)
        return 1.0;
    float3 cam = _VecWorldSpaceCameraPos.xyz;
    float behind = distance(c.worldPos, cam) - distance(StyleDepthReference(c), cam) - b.offset;
    return saturate(0.5 - behind * b.inverseRange);
}

void StyleDepthMask(StyleContext c, inout float mask)
{
    StyleDepth b = LoadStyleDepth(c.slot);
    if (!b.on)
        return;
    mask *= lerp(1.0, StyleDepthTerm(c), b.amount);
}

#endif // GAUSSIAN_STYLE_DEPTH_HLSL
