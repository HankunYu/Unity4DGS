// SPDX-License-Identifier: MIT
// Shape block (GaussianStyleShapeBlock): what the splats are drawn as. One
// choice per style rather than a hook: BakeQuad (SplatUtilities.compute)
// reads the slot and shapes the quad.
#ifndef GAUSSIAN_STYLE_SHAPE_HLSL
#define GAUSSIAN_STYLE_SHAPE_HLSL

// GaussianSplatStyleShape; 0 is the render mode's own shape, for a slot
// without the block
#define STYLE_SHAPE_BASE 0
#define STYLE_SHAPE_DISC 1
#define STYLE_SHAPE_STREAK 2
#define STYLE_SHAPE_SPHERE 3
#define STYLE_SHAPE_CUBE 4
// GaussianSplatStyleStreakAxis
#define STYLE_STREAK_WORLD_AXIS 0
#define STYLE_STREAK_VIEW_RAY 1
#define STYLE_STREAK_VELOCITY 2
#define STYLE_STREAK_SEGMENT 3
#define STYLE_STREAK_SURFACE 4

struct StyleShape
{
    bool on;
    uint shape;             // STYLE_SHAPE_*
    uint streakAxis;        // STYLE_STREAK_*
    float dotRadius;        // pixels
    float3 streakDirection; // world, normalized
    float streakTime;       // seconds, for the Velocity axis
    float streakLength;     // world units, for the World Axis, View Ray and Surface axes
    float streakHalfWidth;  // pixels
    float minWorldSize;     // dot diameter / streak width floor in world units (0 = off)
    float solidSize;        // sphere diameter / cube edge in world units
};
#define STYLE_SHAPE_ROWS 3
float4 _StyleShape[MAX_STYLES * STYLE_SHAPE_ROWS];

// Mirrors GaussianStyleShapeBlock.Data
StyleShape LoadStyleShape(uint slot)
{
    float4 r0 = _StyleShape[slot * STYLE_SHAPE_ROWS];
    float4 r1 = _StyleShape[slot * STYLE_SHAPE_ROWS + 1];
    float4 r2 = _StyleShape[slot * STYLE_SHAPE_ROWS + 2];
    StyleShape b;
    b.on = r0.x > 0.5;
    b.shape = (uint)r0.y;
    b.streakAxis = (uint)r0.z;
    b.dotRadius = r0.w;
    b.streakDirection = r1.xyz;
    b.streakTime = r1.w;
    b.streakLength = r2.x;
    b.streakHalfWidth = r2.y;
    b.minWorldSize = r2.z;
    b.solidSize = r2.w;
    return b;
}

#endif // GAUSSIAN_STYLE_SHAPE_HLSL
