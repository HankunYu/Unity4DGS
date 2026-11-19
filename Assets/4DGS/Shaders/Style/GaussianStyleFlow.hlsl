// SPDX-License-Identifier: MIT
// Flow block (GaussianStyleFlowBlock): waves of light running through the
// splats along a direction.
#ifndef GAUSSIAN_STYLE_FLOW_HLSL
#define GAUSSIAN_STYLE_FLOW_HLSL

struct StyleFlow
{
    bool on;
    float amount;           // how far brightness follows the waves
    float speed;            // world units per second
    float spacing;          // distance between waves
    float3 direction;       // world, normalized
    float columnsPerUnit;
    float tailLength;       // world units
    float stride;           // step the waves advance in (world units, 0 = smooth)
};
#define STYLE_FLOW_ROWS 3
float4 _StyleFlow[MAX_STYLES * STYLE_FLOW_ROWS];

// Mirrors GaussianStyleFlowBlock.Data
StyleFlow LoadStyleFlow(uint slot)
{
    float4 r0 = _StyleFlow[slot * STYLE_FLOW_ROWS];
    float4 r1 = _StyleFlow[slot * STYLE_FLOW_ROWS + 1];
    float4 r2 = _StyleFlow[slot * STYLE_FLOW_ROWS + 2];
    StyleFlow b;
    b.on = r0.x > 0.5;
    b.amount = r0.y;
    b.speed = r0.z;
    b.spacing = r0.w;
    b.direction = r1.xyz;
    b.columnsPerUnit = r1.w;
    b.tailLength = r2.x;
    b.stride = r2.y;
    return b;
}

// Waves run along the flow direction, each lit at its front and fading over
// its tail. Space is cut into columns across the direction, each with its
// own offset and speed, so neighbouring columns never run in step. Rounded,
// so points snapped to a grid of the column size sit at column centres.
void StyleFlowLight(StyleContext c, inout float gain, inout float3 emission)
{
    StyleFlow b = LoadStyleFlow(c.slot);
    if (!b.on || b.amount <= 0)
        return;
    float3 side = normalize(cross(b.direction, abs(b.direction.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
    float3 up = cross(b.direction, side);
    int2 column = (int2)round(float2(dot(c.worldPos, side), dot(c.worldPos, up)) * b.columnsPerUnit);
    uint seed = (uint)column.x * 0x9e3779b1u ^ (uint)column.y * 0x85ebca6bu ^ c.slot;
    float h = StyleHash01(seed);
    float travelled = _StyleTime * b.speed * (0.6 + 0.8 * StyleHash01(seed ^ 0x2c1b3c6du)) + h * b.spacing;
    float along = dot(c.worldPos, b.direction);
    if (b.stride > 0)
    {
        travelled = floor(travelled / b.stride) * b.stride;
        along = round(along / b.stride) * b.stride;
    }
    // Distance the nearest wave front has run past this point
    float behind = frac((travelled - along) / b.spacing) * b.spacing;
    float wave = saturate(1.0 - behind / b.tailLength);
    gain *= lerp(1.0, wave * wave, b.amount);
}

#endif // GAUSSIAN_STYLE_FLOW_HLSL
