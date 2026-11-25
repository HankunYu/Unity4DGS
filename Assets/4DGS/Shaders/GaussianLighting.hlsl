// Scene lighting (GaussianSplatLighting): the scene's lights and ambient
// light the splats like a lit surface, each splat by its own position and
// normal. Mesh-sampled splats carry the outward surface normal as their
// local +Z (as backface culling reads it); round splats have none and take
// the light evenly. View-independent, so every view and both eyes of a
// stereo pair agree. Shared by the view data pass (SplatUtilities.compute)
// and GaussianParticleEmitter, which lights each particle the way its source
// splat is drawn. GaussianLightSet binds the parameters.
//
// The shadow map is the renderer's own splats seen from the shadow light,
// one depth per texel (CSLightShadow* in SplatUtilities.compute), so the
// splats shadow themselves without the source meshes being drawn.
//
// The includer declares _MatrixWorldToObject (the splats' renderer) and
// includes GaussianSplatting.hlsl first.
#ifndef GAUSSIAN_LIGHTING_HLSL
#define GAUSSIAN_LIGHTING_HLSL

// Must match GaussianLightSet.MaxLights
#define MAX_SPLAT_LIGHTS 8

float _LightWeight;         // GaussianSplatLighting.weight; 0 = unlit
uint _LightCount;
// Per light: xyz world position, or the direction towards a directional
// light; w 0 directional, 1 point or spot
float4 _LightPositions[MAX_SPLAT_LIGHTS];
// Per light: rgb linear color times intensity, w 1 for the shadow light
float4 _LightColors[MAX_SPLAT_LIGHTS];
// Per light: xyz direction towards a spot light along its axis; w 1/range²
float4 _LightSpotDirections[MAX_SPLAT_LIGHTS];
// Per light: x spot attenuation scale, y offset (1, 0 for no cone)
float4 _LightSpotParams[MAX_SPLAT_LIGHTS];
// Ambient light as L2 spherical harmonics, packed like unity_SHAr ..
// unity_SHC: Ar, Ag, Ab, Br, Bg, Bb, C
float4 _LightAmbient[7];
// x wrap (terminator softness), y shadow strength
float4 _LightParams;

// Shadow map: world to light space (the shadow light's worldToLocal), and
// x 1 when there is one, y 1 for a perspective (spot) projection, z depth
// bias (world units), w filter radius (texels)
float4x4 _LightShadowMatrix;
float4 _LightShadowParams;
uint _LightShadowResolution;
// LIGHT_SHADOW_HEADER uints of light-space bounds (min x, y, z, max x, y, z
// as sortable uints), then the depth of the nearest splat per texel above
// the minimum z, as float bits
ByteAddressBuffer _LightShadowMap;
#define LIGHT_SHADOW_HEADER 6

// Positive and negative floats ordered as unsigned ints, and back
uint LightSortableUint(float f)
{
    uint u = asuint(f);
    return u ^ (-((int)(u >> 31)) | 0x80000000);
}

float LightSortableFloat(uint u)
{
    return asfloat(u ^ ((u >> 31) != 0 ? 0x80000000 : 0xFFFFFFFF));
}

// A point in light space: xy on the shadow map's plane (divided by depth
// for a spot), z depth
float3 LightShadowSpace(float3 worldPos)
{
    float3 p = mul(_LightShadowMatrix, float4(worldPos, 1)).xyz;
    if (_LightShadowParams.y > 0.5)
        p.xy /= max(p.z, 1e-4);
    return p;
}

// The map's square on the light-space plane from the header's bounds: its
// corner, the size of a texel, and the nearest depth. False before the
// first shadow pass.
//
// The texels sit on a grid fixed in light space: their size is the next
// half-octave step that fits the bounds, and the corner a whole number of
// them. Splats that move change the bounds every frame; a map stretched
// over the bounds would shift all its texels with them, and every shadow
// edge in the scene would land on new texels and flicker. On the fixed grid
// only what moves changes, until the bounds outgrow a step.
bool LightShadowFrameOf(uint3 loBits, uint3 hiBits, out float2 origin, out float texel, out float minZ)
{
    float3 lo = float3(LightSortableFloat(loBits.x), LightSortableFloat(loBits.y), LightSortableFloat(loBits.z));
    float3 hi = float3(LightSortableFloat(hiBits.x), LightSortableFloat(hiBits.y), LightSortableFloat(hiBits.z));
    minZ = lo.z;
    float size = max(hi.x - lo.x, hi.y - lo.y);
    bool valid = all(hi >= lo) && size > 0;
    // One texel is lost to snapping the corner down
    float fit = max(size, 1e-12) / (_LightShadowResolution - 1.0);
    texel = exp2(ceil(log2(fit) * 2.0) * 0.5);
    origin = floor(lo.xy / texel) * texel;
    return valid;
}

bool LightShadowFrame(out float2 origin, out float texel, out float minZ)
{
    return LightShadowFrameOf(_LightShadowMap.Load3(0), _LightShadowMap.Load3(12), origin, texel, minZ);
}

uint LightShadowAddress(uint2 texel)
{
    return (LIGHT_SHADOW_HEADER + texel.y * _LightShadowResolution + texel.x) * 4;
}

// 1 lit, 0 in shadow, filtered over the texels around the splat: a box of
// the filter radius, weighted by how far the splat lies into the texels at
// its rim, so the result changes smoothly as the splat or the shadow moves
float LightShadow(float3 worldPos)
{
    if (_LightShadowParams.x < 0.5)
        return 1.0;
    float2 origin;
    float texel, minZ;
    if (!LightShadowFrame(origin, texel, minZ))
        return 1.0;
    float3 p = LightShadowSpace(worldPos);
    if (_LightShadowParams.y > 0.5 && p.z <= 0)
        return 1.0;
    // Texel centres sit at half coordinates
    float2 at = (p.xy - origin) / texel - 0.5;
    int2 corner = int2(floor(at));
    float2 into = at - floor(at);
    float depth = p.z - minZ - _LightShadowParams.z;
    int radius = (int)_LightShadowParams.w;
    float lit = 0, total = 0;
    for (int y = -radius; y <= radius + 1; y++)
    {
        float wy = y == -radius ? 1.0 - into.y : (y == radius + 1 ? into.y : 1.0);
        for (int x = -radius; x <= radius + 1; x++)
        {
            float wx = x == -radius ? 1.0 - into.x : (x == radius + 1 ? into.x : 1.0);
            float w = wx * wy;
            int2 t = corner + int2(x, y);
            total += w;
            if (any(t < 0) || any(t >= (int)_LightShadowResolution))
            {
                lit += w;
                continue;
            }
            // Empty texels hold the clear value, past any depth
            lit += depth <= asfloat(_LightShadowMap.Load(LightShadowAddress(uint2(t)))) ? w : 0;
        }
    }
    return lit / max(total, 1e-6);
}

float3 LightGammaToLinear(float3 c)
{
    c = max(c, 0);
    return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
}

float3 LightLinearToGamma(float3 c)
{
    c = max(c, 0);
    return c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
}

float3 LightAmbientAt(float3 n)
{
    float4 n1 = float4(n, 1);
    float3 l1 = float3(dot(_LightAmbient[0], n1), dot(_LightAmbient[1], n1), dot(_LightAmbient[2], n1));
    float4 b = n.xyzz * n.yzzx;
    float3 l2 = float3(dot(_LightAmbient[3], b), dot(_LightAmbient[4], b), dot(_LightAmbient[5], b));
    return max(l1 + l2 + _LightAmbient[6].rgb * (n.x * n.x - n.y * n.y), 0);
}

// The color the scene's lights give a splat of this color (gamma space,
// before the style), blended in by the lighting weight
half3 LightSplat(half3 color, float4 rot, float3 scale, float3 worldPos)
{
    if (_LightWeight <= 0)
        return color;

    float3 s = abs(scale);
    float minScale = min(s.x, min(s.y, s.z));
    float maxScale = max(s.x, max(s.y, s.z));
    float midScale = s.x + s.y + s.z - minScale - maxScale;
    float flatness = 1.0 - minScale / max(midScale, 1e-8);
    // Inverse transpose, as for the backface normal
    float3 n = normalize(mul(QuatRotateVector(float3(0, 0, 1), rot), (float3x3)_MatrixWorldToObject));

    float wrap = _LightParams.x;
    float3 light = LightAmbientAt(n);
    for (uint i = 0; i < _LightCount; i++)
    {
        float4 pos = _LightPositions[i];
        float3 toLight = pos.xyz - worldPos * pos.w;
        float distSq = max(dot(toLight, toLight), 1e-4);
        float3 l = toLight * rsqrt(distSq);
        // URP's attenuation: inverse square, smoothly cut at the range
        float atten = 1.0;
        if (pos.w > 0.5)
        {
            float4 spot = _LightSpotDirections[i];
            float f = distSq * spot.w;
            float smooth = saturate(1.0 - f * f);
            float cone = saturate(dot(spot.xyz, l) * _LightSpotParams[i].x + _LightSpotParams[i].y);
            atten = smooth * smooth / distSq * cone * cone;
        }
        float4 c = _LightColors[i];
        if (c.w > 0.5)
            atten *= lerp(1.0, LightShadow(worldPos), _LightParams.y);
        // Wrapped Lambert, softening the terminator; round splats take the
        // light evenly
        float diffuse = saturate((dot(n, l) + wrap) / (1.0 + wrap));
        diffuse = lerp(0.5, diffuse, flatness);
        light += c.rgb * (diffuse * atten);
    }

    float3 lit = LightLinearToGamma(LightGammaToLinear(color) * light);
    return lerp(color, (half3)lit, (half)_LightWeight);
}

#endif // GAUSSIAN_LIGHTING_HLSL
