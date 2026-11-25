// SPDX-License-Identifier: MIT
// Flow block (GaussianStyleFlowBlock): waves of light running through the
// splats along a direction, through columns of world space, strips of the
// surface or strips of the mesh's UV space.
#ifndef GAUSSIAN_STYLE_FLOW_HLSL
#define GAUSSIAN_STYLE_FLOW_HLSL

// Mirrors GaussianSplatStyleFlowSpace
#define STYLE_FLOW_WORLD 0
#define STYLE_FLOW_SURFACE 1
#define STYLE_FLOW_UV 2

struct StyleFlow
{
    bool on;
    float amount;           // how far brightness follows the waves
    float speed;            // units per second: world, or UV in UV space
    float spacing;          // distance between waves
    float3 direction;       // normalized: world, or U and V in its xy in UV space
    float columnsPerUnit;
    float tailLength;
    float stride;           // step the waves advance in (0 = smooth)
    uint space;             // STYLE_FLOW_*
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
    b.space = (uint)r2.z;
    return b;
}

// UVs of the splats (GaussianMeshBinding): a float2 per splat, far below
// zero on a mesh without UVs. Bound by GaussianSplatRenderer.SetAssetDataOnCS.
StructuredBuffer<float2> _SplatUv;
uint _SplatUvValid;

// Where a splat sits in the flow
struct StyleFlowPlace
{
    float along;    // how far along the direction
    int2 column;    // the column it is in
    uint family;    // tells apart columns counted along different axes
};

// Tangent directions the strips of a surface are counted along, over half a
// turn: a curved surface gets runs of parallel strips instead of strips
// that change width with every splat's normal
#define STYLE_FLOW_SURFACE_TURNS 16
// How far a surface's normal may lean from the flow direction and the
// surface still count as lying across it (sine of the angle)
#define STYLE_FLOW_ACROSS 0.6

// Straight columns of world space: cells across the direction
StyleFlowPlace StyleFlowWorld(StyleFlow b, float3 worldPos, float3 side, float3 up)
{
    StyleFlowPlace place;
    place.along = dot(worldPos, b.direction);
    place.column = (int2)round(float2(dot(worldPos, side), dot(worldPos, up)) * b.columnsPerUnit);
    place.family = 0;
    return place;
}

// Strips lying in the splat's surface. Down a surface the flow can run
// along, they are counted along its tangent across the flow, so they are
// even at any angle the surface stands at. Over a surface lying across the
// flow, where nothing runs along the direction, they run sideways instead.
StyleFlowPlace StyleFlowSurface(StyleContext c, StyleFlow b, float3 side, float3 up)
{
    float flatness;
    float3 normal = StyleWorldNormal(c.rot, c.scale, flatness);
    float2 across = float2(dot(c.worldPos, side), dot(c.worldPos, up));
    float2 lean = float2(dot(normal, side), dot(normal, up));
    StyleFlowPlace place;
    if (length(lean) < STYLE_FLOW_ACROSS)
    {
        place.along = across.x;
        place.column = int2((int)round(across.y * b.columnsPerUnit), 0);
        place.family = 1;
        return place;
    }
    static const float Turn = 3.14159265 / STYLE_FLOW_SURFACE_TURNS;
    float turn = round(atan2(lean.y, lean.x) / Turn);
    float angle = turn * Turn;
    float2 tangent = float2(-sin(angle), cos(angle));
    place.along = dot(c.worldPos, b.direction);
    place.column = int2((int)round(dot(across, tangent) * b.columnsPerUnit), 0);
    place.family = 2 + (uint)(turn + STYLE_FLOW_SURFACE_TURNS);
    return place;
}

// Strips of the mesh's UV space; false for a splat without UVs
bool StyleFlowUv(StyleContext c, StyleFlow b, out StyleFlowPlace place)
{
    place.along = 0;
    place.column = 0;
    place.family = 0;
    if (_SplatUvValid == 0)
        return false;
    float2 uv = _SplatUv[c.idx];
    if (uv.x < -1e20)
        return false;
    float2 direction = b.direction.xy;
    place.along = dot(uv, direction);
    place.column = int2((int)round(dot(uv, float2(-direction.y, direction.x)) * b.columnsPerUnit), 0);
    place.family = 0xffffu;
    return true;
}

// Waves run along the flow direction, each lit at its front and fading over
// its tail. Its space is cut into columns across the direction, each with
// its own offset and speed, so neighbouring columns never run in step.
// Rounded, so points snapped to a grid of the column size sit at column
// centres.
void StyleFlowLight(StyleContext c, inout float gain, inout float3 emission)
{
    StyleFlow b = LoadStyleFlow(c.slot);
    if (!b.on || b.amount <= 0)
        return;
    float3 side = normalize(cross(b.direction, abs(b.direction.y) < 0.9 ? float3(0, 1, 0) : float3(1, 0, 0)));
    float3 up = cross(b.direction, side);
    StyleFlowPlace place;
    if (b.space == STYLE_FLOW_SURFACE)
        place = StyleFlowSurface(c, b, side, up);
    else if (b.space != STYLE_FLOW_UV || !StyleFlowUv(c, b, place))
        place = StyleFlowWorld(b, c.worldPos, side, up);
    uint seed = (uint)place.column.x * 0x9e3779b1u ^ (uint)place.column.y * 0x85ebca6bu ^ c.slot ^ place.family * 0x27d4eb2fu;
    float h = StyleHash01(seed);
    float travelled = _StyleTime * b.speed * (0.6 + 0.8 * StyleHash01(seed ^ 0x2c1b3c6du)) + h * b.spacing;
    float along = place.along;
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
