// SPDX-License-Identifier: MIT
// Shared code for the passes of RenderGaussianStylized.shader, the quad
// shader. CSCalcViewData already baked each splat's color and quad into the
// view data (BakeQuad); what is left here is the draw-side part: the opacity
// profile of the render mode's base shape (Gaussian or point) morphing into
// the style's shape, and the blend. At zero style weight it draws what the
// render mode alone would.
#ifndef RENDER_GAUSSIAN_STYLIZED_PASS_HLSL
#define RENDER_GAUSSIAN_STYLIZED_PASS_HLSL

#include "RenderGaussianSplatsPass.hlsl"

// Matches GaussianSplatStyleRange: per style slot (GaussianStyleSet), the
// additivity at full weight, GaussianSplatStyleShape and streak tail power
struct SplatStyleRange
{
    uint start;
    uint baked;
    uint pad0;
    uint pad1;
    float4 additivity;
    float4 shape;
    float4 streakTailPower;
    float4 solidSize;
    float4 solidShading;
    float4 solidSizeVariation;
};

// One entry per renderer in the draw, ascending by start. A global draw
// merges several renderers into one sorted buffer; a per-object draw has one.
StructuredBuffer<SplatStyleRange> _StyleRanges;
uint _StyleRangeCount;

// A splat's own number in [0, 1), the same every frame
float QuadSplatRandom (uint splatIdx)
{
	uint x = splatIdx * 0x9e3779b9u + 0x85ebca6bu;
	x ^= x >> 16; x *= 0x7feb352du;
	x ^= x >> 15; x *= 0x846ca68bu;
	x ^= x >> 16;
	return (x >> 8) * (1.0 / 16777216.0);
}

SplatStyleRange StyleRangeFor (uint splatIdx)
{
	// A draw holds a handful of renderers: a linear scan is enough
	uint r = 0;
	for (uint i = 1; i < _StyleRangeCount && _StyleRanges[i].start <= splatIdx; i++)
		r = i;
	return _StyleRanges[r];
}

#define STYLE_PLAIN_SHAPE -1.0
// GaussianSplatStyleShape
#define STYLE_SHAPE_DISC 1
#define STYLE_SHAPE_SPHERE 3
#define STYLE_SHAPE_CUBE 4

// For the solid shapes (Sphere, Cube), which the fragment traces in world
// space: the inverse of the view-projection the view data was made with,
// the eye, and whether both hold (not under stereo or ODS, where a solid
// shape falls back to a disc). Set with the view data.
float4x4 _QuadInvViewProjection;
float4 _QuadEyePosition;
uint _QuadRaysValid;
// The light the solid shapes are shaded by (GaussianLightSet): xyz world
// position, or the direction towards a directional light; w 0 directional,
// 1 point or spot. All zero without scene lighting.
float4 _QuadKeyLight;

// A baked quad's color (PackBakedViewColor), linear like SplatViewColor's,
// and its splat's style weight and slot
half4 BakedViewColor (SplatViewData view, out float styleWeight, out uint styleSlot)
{
	half4 col = half4(UnpackRGB9E5(view.color.x), ViewAlpha(view.color));
	#if !defined(UNITY_COLORSPACE_GAMMA)
	col.rgb = GammaToLinearSpace(col.rgb);
	#endif
	styleWeight = BakedViewStyleWeight(view.color);
	styleSlot = BakedViewStyleSlot(view.color);
	return col;
}

// Base shape of baked quads: 0 = Gaussian (Splats mode), 1 = point (PointCloud)
uint _QuadBaseShape;

struct v2fStylized
{
	half4 col : COLOR0;
	// Plain splats: Gaussian units, as in v2f. Baked: quad position in [-1, 1],
	// x running from the splat (-1) to a streak's tail (+1).
	float2 pos : TEXCOORD0;
	float4 vertex : SV_POSITION;
	// additivity, shape weight, streak tail power, shape
	// (GaussianSplatStyleShape; STYLE_PLAIN_SHAPE for a plain splat)
	nointerpolation float4 style : TEXCOORD1;
	// Eye-to-splat distance, for the depth AOV: clip w in the perspective
	// path, the ODS solve's distance in the omnidirectional one
	float viewDist : TEXCOORD2;
	// Solid shapes: the shape's centre in world space and its size (sphere
	// diameter, cube edge); the eye ray through this fragment, unnormalized;
	// how far the shape's own form swings its color (SolidShapeShade)
	nointerpolation float4 solid : TEXCOORD3;
	float3 ray : TEXCOORD4;
	nointerpolation float solidShading : TEXCOORD5;
	#if defined(GAUSSIAN_LAYERED_EYES) && defined(SHADER_API_METAL)
	// Eye texture array slice, as in v2f
	uint rtIndex : SV_RenderTargetArrayIndex;
	#endif
};

v2fStylized StylizedVert (uint vtxID, uint instID)
{
	uint splatIdx = _OrderBuffer[instID];
	SplatStyleRange range = StyleRangeFor(splatIdx);
	v2fStylized o = (v2fStylized)0;
	#if defined(GAUSSIAN_LAYERED_EYES) && defined(SHADER_API_METAL)
	o.rtIndex = _EyeIndex;
	#endif

	if (range.baked == 0)
	{
		// A plain renderer sharing a global draw with styled ones
		v2f s = SplatVert(vtxID, instID);
		o.col = s.col;
		o.pos = s.pos;
		o.vertex = s.vertex;
		o.style = float4(0, 0, 1, STYLE_PLAIN_SHAPE);
	}
	else
	{
		SplatViewData view = _SplatViewData[_IsStereo ? splatIdx * 2 + _EyeIndex : splatIdx];
		if (view.pos.w <= 0)
		{
			o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
		}
		else
		{
			float styleWeight;
			uint slot;
			o.col = BakedViewColor(view, styleWeight, slot);
			// The view data holds the quad's half-axes in pixels as they are
			float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
			o.pos = quadPos;
			float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _VecScreenParams.xy;
			o.vertex = view.pos;
			o.vertex.xy += deltaScreenPos * view.pos.w;
			float shape = range.shape[slot];
			if (shape >= STYLE_SHAPE_SPHERE)
			{
				if (_QuadRaysValid != 0)
				{
					// The quad's four corners share the centre's w, so the
					// rays through them interpolate exactly
					float4 corner = mul(_QuadInvViewProjection, o.vertex);
					float4 center = mul(_QuadInvViewProjection, view.pos);
					o.ray = corner.xyz / corner.w - _QuadEyePosition.xyz;
					// The baked quad covers the full size; each splat's
					// own shape may be smaller
					float size = range.solidSize[slot] * (1.0 - range.solidSizeVariation[slot] * QuadSplatRandom(splatIdx - range.start));
					o.solid = float4(center.xyz / center.w, size);
					o.solidShading = range.solidShading[slot];
				}
				else
				{
					shape = STYLE_SHAPE_DISC;
				}
			}
			o.style = float4(range.additivity[slot] * styleWeight, styleWeight, range.streakTailPower[slot], shape);
			o.viewDist = view.pos.w;
		}
		// SplatVert flips the plain branch itself
		if (!_IsStereo)
			FlipProjectionIfBackbuffer(o.vertex);
	}
	return o;
}

// What an eye ray meets of a solid shape (Sphere, Cube): how much of the
// pixel the shape covers, and the surface normal there, in world space.
// Derivatives are taken here, so call it before any branch or discard.
half SolidShapeCoverage (v2fStylized i, out float3 normal)
{
	float3 ro = _QuadEyePosition.xyz - i.solid.xyz;
	float3 rd = normalize(i.ray);
	float halfSize = i.solid.w * 0.5;

	// Sphere: by how far the ray passes from the centre
	float along = dot(ro, rd);
	float3 nearest = ro - along * rd;
	float miss = length(nearest) / max(halfSize, 1e-8);
	half sphereCoverage = 1.0 - smoothstep(1.0 - max(fwidth(miss), 1e-5), 1.0, miss);
	float tSphere = -along - halfSize * sqrt(saturate(1.0 - miss * miss));
	float3 sphereNormal = normalize(ro + tSphere * rd);

	// Cube along the world axes: the slabs' overlap along the ray, which
	// shrinks to nothing at the silhouette
	float3 inv = 1.0 / (abs(rd) > 1e-6 ? rd : 1e-6);
	float3 t0 = (-halfSize - ro) * inv;
	float3 t1 = (halfSize - ro) * inv;
	float3 tNear = min(t0, t1);
	float3 tFar = max(t0, t1);
	float tIn = max(tNear.x, max(tNear.y, tNear.z));
	float tOut = min(tFar.x, min(tFar.y, tFar.z));
	float inside = tOut - tIn;
	half cubeCoverage = tOut > 0 ? saturate(inside / max(fwidth(inside), 1e-7)) : 0;
	float3 cubeNormal = -sign(rd) * (tNear.x == tIn ? float3(1, 0, 0) : (tNear.y == tIn ? float3(0, 1, 0) : float3(0, 0, 1)));

	bool cube = (uint)round(i.style.w) == STYLE_SHAPE_CUBE;
	normal = cube ? cubeNormal : sphereNormal;
	return cube ? cubeCoverage : sphereCoverage;
}

// What a solid shape's own form does to its color where its surface faces
// this way. The color is the splat's, lit as the model is there; the form
// swings around it by the shading, brighter towards the key light and
// darker away from it, so the shape as a whole keeps the model's light.
half SolidShapeShade (v2fStylized i, float3 normal)
{
	float3 toLight = _QuadKeyLight.w > 0.5 ? _QuadKeyLight.xyz - i.solid.xyz : _QuadKeyLight.xyz;
	// Without scene lighting: from above, over the shoulder
	if (dot(toLight, toLight) < 1e-8)
		toLight = float3(0.35, 0.85, 0.4);
	return max(0.0h, 1.0h + (half)i.solidShading * (half)dot(normal, normalize(toLight)));
}

// Alpha and coverage of a baked quad. The base keeps its own render mode's
// rule -- a Gaussian scales opacity before clamping it, a point takes its
// disc's coverage of an opacity BakeQuad already boosted and clamped -- and
// the style's shapes are coverage profiles, like the point. The style weight
// morphs the base into the shape.
// aa is the width of a one-pixel transition band in quad units, taken by
// the caller before any branch (see StylizedColor), and so are a solid
// shape's coverage and normal. shade is the share of its color the quad
// keeps at this fragment: a solid shape's shading, else 1.
void StylizedBakedProfile (v2fStylized i, float aa, half solidCoverage, float3 solidNormal, out half alpha, out half coverage, out half shade)
{
	float2 q = i.pos;
	float r = length(q);

	SplatManualDepthTest(i.vertex);

	// Round with an antialiased rim. A binary discard would leave hard
	// discs that alias at small sizes and shimmer when the frame is
	// resampled (cubemap -> equirect); MSAA cannot help, since the shader
	// carves the circle.
	half disc = 1.0 - smoothstep(1.0 - aa, 1.0, r);
	bool pointBase = _QuadBaseShape == 1;
	// abs() ignores the editor "selected splat" negative-alpha encoding
	half opacity = abs(i.col.a);
	half solid = saturate(opacity);

	// The base: the point, or the splat's own Gaussian in the units the
	// plain quad uses
	half baseCoverage = pointBase ? disc : exp(-dot(q, q) * _SplatQuadExtent * _SplatQuadExtent);
	half baseAlpha = pointBase ? solid * disc : saturate(baseCoverage * opacity);

	half shapeCoverage = baseCoverage;
	half shapeAlpha = baseAlpha;
	half shapeShade = 1.0;
	uint shapeId = (uint)round(i.style.w);
	if (shapeId != 0)
	{
		if (shapeId == STYLE_SHAPE_DISC)
		{
			shapeCoverage = disc;
		}
		else if (shapeId >= STYLE_SHAPE_SPHERE)
		{
			shapeCoverage = solidCoverage;
			shapeShade = SolidShapeShade(i, solidNormal);
		}
		else
		{
			// Brightest at the splat, fading to the tail; a tent across keeps
			// a line a pixel or two wide from aliasing
			float t = q.x * 0.5 + 0.5;
			shapeCoverage = pow(saturate(1.0 - t), i.style.z) * saturate(1.0 - abs(q.y));
		}
		shapeAlpha = solid * shapeCoverage;
	}
	half w = (half)i.style.y;
	alpha = lerp(baseAlpha, shapeAlpha, w);
	coverage = lerp(baseCoverage, shapeCoverage, w);
	shade = lerp(1.0h, shapeShade, w);
}

half4 StylizedColor (v2fStylized i)
{
	// Screen-space derivatives are undefined inside non-uniform control flow
	// and below a discard, and some compilers then return zero, which would
	// turn every antialiased rim hard. Take them here, first thing.
	float aa = max(fwidth(length(i.pos)), 1e-5);
	float3 solidNormal;
	half solidCoverage = SolidShapeCoverage(i, solidNormal);

	#if defined(GAUSSIAN_POINT_AOV)
	{
		// Depth AOV (PointCloud mode, every quad baked). Z is not a blendable
		// quantity and this target has no depth buffer, so nearest-wins is
		// done by the blend unit: emit reciprocal distance under BlendOp Max
		// and the closest splat takes the pixel regardless of draw order. The
		// target clears to zero, which reads as infinitely far. The composite
		// inverts it back to metres.
		//
		// Coverage is thresholded rather than multiplied in: a depth cannot be
		// partially blended, so the matte is hard by construction. Every
		// pixel of a dot or streak carries its splat's distance.
		half alpha, coverage, shade;
		StylizedBakedProfile(i, aa, solidCoverage, solidNormal, alpha, coverage, shade);
		if (coverage < 0.5)
			discard;
		float invDist = 1.0 / max(i.viewDist, 1e-6);
		return half4(invDist, invDist, invDist, 1);
	}
	#endif

	half4 col;
	if (i.style.w == STYLE_PLAIN_SHAPE)
	{
		v2f s;
		s.col = i.col;
		s.pos = i.pos;
		s.vertex = i.vertex;
		col = SplatColor(s);
	}
	else
	{
		half alpha, coverage, shade;
		StylizedBakedProfile(i, aa, solidCoverage, solidNormal, alpha, coverage, shade);
		if (SplatContribution(alpha, i.col.rgb) < _SplatAlphaCutoff)
			discard;
		col = half4(i.col.rgb * (shade * alpha), alpha);
	}
	// Premultiplied color: lowering alpha alone turns both the offscreen
	// under-blend and the direct over-blend into an add, as the splat stops
	// occluding what is drawn after (or before) it.
	col.a *= 1.0h - (half)i.style.x;
	return col;
}

#endif // RENDER_GAUSSIAN_STYLIZED_PASS_HLSL
