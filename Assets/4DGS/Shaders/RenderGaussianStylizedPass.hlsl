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

// Matches GaussianSplatStyleRange
struct SplatStyleRange
{
    uint start;
    float additivity;
    uint baked;
    uint shape;
    float streakTailPower;
    float pad0;
    float pad1;
    float pad2;
};

// One entry per renderer in the draw, ascending by start. A global draw
// merges several renderers into one sorted buffer; a per-object draw has one.
StructuredBuffer<SplatStyleRange> _StyleRanges;
uint _StyleRangeCount;

SplatStyleRange StyleRangeFor (uint splatIdx)
{
	// A draw holds a handful of renderers: a linear scan is enough
	uint r = 0;
	for (uint i = 1; i < _StyleRangeCount && _StyleRanges[i].start <= splatIdx; i++)
		r = i;
	return _StyleRanges[r];
}

#define STYLE_PLAIN_SHAPE -1.0

// A baked quad's color (PackBakedViewColor), linear like SplatViewColor's,
// and its splat's style weight
half4 BakedViewColor (SplatViewData view, out float styleWeight)
{
	half4 col = half4(UnpackRGB9E5(view.color.x), ViewAlpha(view.color));
	#if !defined(UNITY_COLORSPACE_GAMMA)
	col.rgb = GammaToLinearSpace(col.rgb);
	#endif
	styleWeight = BakedViewStyleWeight(view.color);
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
};

v2fStylized StylizedVert (uint vtxID, uint instID)
{
	uint splatIdx = _OrderBuffer[instID];
	SplatStyleRange range = StyleRangeFor(splatIdx);
	v2fStylized o = (v2fStylized)0;

	if (range.baked == 0)
	{
		// A plain renderer sharing a global draw with styled ones
		v2f s = SplatVert(vtxID, instID);
		o.col = s.col;
		o.pos = s.pos;
		o.vertex = s.vertex;
		o.style = float4(range.additivity, 0, 1, STYLE_PLAIN_SHAPE);
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
			o.col = BakedViewColor(view, styleWeight);
			// The view data holds the quad's half-axes in pixels as they are
			float2 quadPos = float2(vtxID & 1, (vtxID >> 1) & 1) * 2.0 - 1.0;
			o.pos = quadPos;
			float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _VecScreenParams.xy;
			o.vertex = view.pos;
			o.vertex.xy += deltaScreenPos * view.pos.w;
			o.style = float4(range.additivity * styleWeight, styleWeight, range.streakTailPower, range.shape);
			o.viewDist = view.pos.w;
		}
		// SplatVert flips the plain branch itself
		if (!_IsStereo)
			FlipProjectionIfBackbuffer(o.vertex);
	}
	return o;
}

// Alpha and coverage of a baked quad. The base keeps its own render mode's
// rule -- a Gaussian scales opacity before clamping it, a point takes its
// disc's coverage of an opacity BakeQuad already boosted and clamped -- and
// the style's shapes are coverage profiles, like the point. The style weight
// morphs the base into the shape.
// aa is the width of a one-pixel transition band in quad units, taken by
// the caller before any branch (see StylizedColor).
void StylizedBakedProfile (v2fStylized i, float aa, out half alpha, out half coverage)
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
	uint shapeId = (uint)round(i.style.w);
	if (shapeId != 0)
	{
		if (shapeId == 1)
		{
			shapeCoverage = disc;
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
}

half4 StylizedColor (v2fStylized i)
{
	// Screen-space derivatives are undefined inside non-uniform control flow
	// and below a discard, and some compilers then return zero, which would
	// turn every antialiased rim hard. Take them here, first thing.
	float aa = max(fwidth(length(i.pos)), 1e-5);

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
		half alpha, coverage;
		StylizedBakedProfile(i, aa, alpha, coverage);
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
		half alpha, coverage;
		StylizedBakedProfile(i, aa, alpha, coverage);
		if (SplatContribution(alpha, i.col.rgb) < _SplatAlphaCutoff)
			discard;
		col = half4(i.col.rgb * alpha, alpha);
	}
	// Premultiplied color: lowering alpha alone turns both the offscreen
	// under-blend and the direct over-blend into an add, as the splat stops
	// occluding what is drawn after (or before) it.
	col.a *= 1.0h - (half)i.style.x;
	return col;
}

#endif // RENDER_GAUSSIAN_STYLIZED_PASS_HLSL
