// SPDX-License-Identifier: MIT
// Shared vertex/fragment code for the passes of RenderGaussianSplats.shader.
// Each pass declares its own pragmas and includes the foveated-rendering
// keywords before this file, which GaussianSplatting.hlsl depends on.
#ifndef RENDER_GAUSSIAN_SPLATS_PASS_HLSL
#define RENDER_GAUSSIAN_SPLATS_PASS_HLSL

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
};

StructuredBuffer<SplatViewData> _SplatViewData;
float4 _VecScreenParams;
uint _EyeIndex;
uint _IsStereo;

// Quad extent, in the units of the fragment's exp(-dot(pos,pos)) falloff
// (one unit = sqrt(2) sigma). Set globally from GaussianSplatConfig.
float _SplatQuadExtent;
// Non-zero: shrink each quad to where its own opacity falls below the
// fragment discard threshold. Removes only fragments that would be
// discarded anyway, so it never changes the image.
uint _SplatAdaptiveExtent;
// Largest on-screen radius, in pixels, a splat may grow to. Larger ones
// are scaled down rather than cut, so they keep their soft edge.
float _SplatMaxPixelRadius;

// Depth texture for manual mesh occlusion when VRR is active.
// Hardware depth test fails under VRR because the depth buffer uses a
// different rasterization rate map than our intermediate splat RT.
#if defined(GAUSSIAN_STEREO_DEPTH)
Texture2DArray _GaussianDepthTex;
#endif

#define ALPHA_DISCARD (5.0 / 255.0)

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;
    instID = _OrderBuffer[instID];
	uint viewIndex = _IsStereo ? instID * 2 + _EyeIndex : instID;
	SplatViewData view = _SplatViewData[viewIndex];
	float4 centerClipPos = view.pos;
	bool behindCam = centerClipPos.w <= 0;
	if (behindCam)
	{
		o.vertex = asfloat(0x7fc00000); // NaN discards the primitive
	}
	else
	{
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);
		// Every pass blends in linear space
		#if !defined(UNITY_COLORSPACE_GAMMA)
		// The color is constant across the quad, so converting it once per
		// vertex matches converting the un-premultiplied color per fragment.
		o.col.rgb = GammaToLinearSpace(o.col.rgb);
		#endif

		float extent = _SplatQuadExtent;
		// alpha * exp(-r^2) drops below the discard threshold at
		// r^2 = ln(alpha / threshold); nothing beyond that is ever drawn.
		// Selected splats (negative alpha) draw an outline past it, so they
		// keep the full quad.
		if (_SplatAdaptiveExtent != 0 && o.col.a >= 0)
			extent = min(extent, sqrt(max(log(o.col.a / ALPHA_DISCARD), 0)));

		float2 axis1 = view.axis1;
		float2 axis2 = view.axis2;
		float radius1 = length(axis1) * extent;
		float radius2 = length(axis2) * extent;
		if (radius1 > _SplatMaxPixelRadius)
			axis1 *= _SplatMaxPixelRadius / radius1;
		if (radius2 > _SplatMaxPixelRadius)
			axis2 *= _SplatMaxPixelRadius / radius2;

		uint idx = vtxID;
		float2 quadPos = (float2(idx&1, (idx>>1)&1) * 2.0 - 1.0) * extent;

		o.pos = quadPos;

		float2 deltaScreenPos = (quadPos.x * axis1 + quadPos.y * axis2) * 2 / _VecScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;

	}
	// In stereo mode we always render to an intermediate RT, never the
	// backbuffer, so skip the backbuffer Y-flip detection.
	if (!_IsStereo)
		FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

// Premultiplied linear splat color.
half4 SplatColor (v2f i)
{
	// Manual depth test for mesh occlusion under VRR.
	// The depth buffer uses the camera's VRR rate map, but our splat RT may
	// not. Remap our linear fragment coords to non-uniform to sample depth.
	#if defined(GAUSSIAN_STEREO_DEPTH)
	{
		float2 uv = i.vertex.xy / _VecScreenParams.xy;
		float2 depthUV = GaussianRemapLinearToNonUniform(uv, _EyeIndex);
		int2 depthCoord = int2(depthUV * _VecScreenParams.xy);
		float meshDepth = _GaussianDepthTex.Load(int4(depthCoord, _EyeIndex, 0)).r;
		// Reversed-Z on Metal: near=1, far=0. Fragment behind mesh → discard.
		float splatDepth = i.vertex.z;
		if (splatDepth < meshDepth)
			discard;
	}
	#endif

	half power = -dot(i.pos, i.pos);
	half alpha = exp(power);
	if (i.col.a >= 0)
	{
		alpha = saturate(alpha * i.col.a);
	}
	else
	{
		// "selected" splat: magenta outline, increase opacity, magenta tint
		half3 selectedColor = half3(1,0,1);
		if (alpha > 7.0/255.0)
		{
			if (alpha < 10.0/255.0)
			{
				alpha = 1;
				i.col.rgb = selectedColor;
			}
			alpha = saturate(alpha + 0.3);
		}
		i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
	}

    if (alpha < ALPHA_DISCARD)
        discard;

    return half4(i.col.rgb * alpha, alpha);
}

#endif // RENDER_GAUSSIAN_SPLATS_PASS_HLSL
