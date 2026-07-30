// SPDX-License-Identifier: MIT
// Lightweight point cloud render mode. Reads the same sorted view data as the
// splat shader (_OrderBuffer -> _SplatViewData), so all animation, morph,
// modifier and cutout effects baked by CSCalcViewData are preserved. Draws
// round points sized by each splat's projected footprint (clamped + scaled).
Shader "Gaussian Splatting/Render Point Cloud"
{
    // Blend state is material-driven so the AOV variant can switch to BlendOp Max
    // without a second pass — which would mean threading a pass index through
    // every DrawProcedural site. Defaults are the beauty under-blend.
    Properties
    {
        [HideInInspector] _SrcBlend ("", Float) = 8   // OneMinusDstAlpha
        [HideInInspector] _DstBlend ("", Float) = 1   // One
        [HideInInspector] _BlendOp  ("", Float) = 0   // Add
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            BlendOp [_BlendOp]
            Blend [_SrcBlend] [_DstBlend]
            Cull Off

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
// Async-compile placeholder breaks DrawProcedural vertex pulling on Vulkan
// ("Shader requires a compute buffer ... none provided" on first use).
#pragma editor_sync_compilation
#pragma require 2darray
#pragma multi_compile _ GAUSSIAN_STEREO_DEPTH
// Global (not _local): Material.EnableKeyword is CPU-immediate and would be
// cleared before the render graph executes the deferred draw.
#pragma multi_compile _ GAUSSIAN_POINT_AOV

// Enable foveated rendering (VRR) support on visionOS Metal.
#include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"

#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
    // Eye-to-splat distance: clip w in the perspective path, the ODS solve's
    // distance in the omnidirectional one. Carried for the depth AOV.
    float viewDist : TEXCOORD1;
};

StructuredBuffer<SplatViewData> _SplatViewData;
float4 _VecScreenParams;
uint _EyeIndex;
uint _IsStereo;
float _PointSizeScale;
float _PointMinSize;
float _PointMinWorldSize;
// Camera projection Y scale (cot(fovY/2)), bound from C#. The built-in
// UNITY_MATRIX_P is unreliable in this DrawProcedural pass (Y-flipped /
// negative m11 when rendering into an intermediate RT).
float _PointProjectionScale;
float _PointMaxSize;
float _PointOpacityBoost;

// Depth texture for manual mesh occlusion when VRR is active.
// Hardware depth test fails under VRR because the depth buffer uses a
// different rasterization rate map than our intermediate splat RT.
#if defined(GAUSSIAN_STEREO_DEPTH)
Texture2DArray _GaussianDepthTex;
#endif

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
		o.viewDist = centerClipPos.w;
		o.col.r = f16tof32(view.color.x >> 16);
		o.col.g = f16tof32(view.color.x);
		o.col.b = f16tof32(view.color.y >> 16);
		o.col.a = f16tof32(view.color.y);

		uint idx = vtxID;
		float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
		o.pos = quadPos;

		// Follow each splat's projected footprint (~1 sigma, pixel units) so
		// nearby points keep surface coverage instead of dissolving into
		// sparse dots; clamp between min and max size to keep the point
		// cloud look. Also makes scale-driven modifier effects shrink points.
		float footprint = sqrt(length(view.axis1) * length(view.axis2));
		float sizePx = clamp(footprint * _PointSizeScale, _PointMinSize, _PointMaxSize);

		// World-space size floor (diameter in meters): keeps points from
		// shrinking into dust as the camera moves close, where grazing-angle
		// splats collapse in projected size. Overrides the max clamp, since
		// it only kicks in near the camera. 0 disables.
		if (_PointMinWorldSize > 0)
		{
			float worldMinPx = _PointMinWorldSize * 0.5 * _PointProjectionScale * _VecScreenParams.y / centerClipPos.w;
			sizePx = max(sizePx, worldMinPx);
		}

		o.vertex = centerClipPos;
		o.vertex.xy += (quadPos * sizePx / _VecScreenParams.xy) * centerClipPos.w;
	}
	// In stereo mode we always render to an intermediate RT, never the
	// backbuffer, so skip the backbuffer Y-flip detection.
	if (!_IsStereo)
		FlipProjectionIfBackbuffer(o.vertex);
    return o;
}

half4 frag (v2f i) : SV_Target
{
	// Round point mask with analytic edge coverage. A binary discard leaves
	// hard-edged discs that alias badly at small point sizes and shimmer when
	// the frame is resampled (cubemap -> equirect capture). MSAA cannot fix
	// that: the circle is carved by the shader, so discard kills every sample
	// of the pixel and only the (already discarded) quad corners get coverage.
	// The quad spans sizePx pixels across, so fwidth gives the width of a
	// one-pixel transition band directly in i.pos units.
	//
	// Kept above every discard below: screen-space derivatives are only well
	// defined while the whole 2x2 pixel quad is still live.
	float r = length(i.pos);
	float aa = max(fwidth(r), 1e-5);
	float coverage = 1.0 - smoothstep(1.0 - aa, 1.0, r);

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

	if (coverage <= 0.0)
		discard;

	#if defined(GAUSSIAN_POINT_AOV)
	{
		// Depth AOV. Z is not a blendable quantity, and this render target has
		// no depth buffer to resolve it with, so nearest-wins is done by the
		// blend unit: emit reciprocal distance under BlendOp Max, and the
		// closest point takes the pixel regardless of draw order. The target
		// clears to zero, which reads as infinitely far — exactly the wanted
		// background value. The composite inverts it back to metres.
		//
		// Coverage is thresholded rather than multiplied in: a depth cannot be
		// partially blended, so the matte is hard by construction.
		if (coverage < 0.5)
			discard;
		float invDist = 1.0 / max(i.viewDist, 1e-6);
		return half4(invDist, invDist, invDist, 1);
	}
	#endif

	// abs() ignores the editor "selected splat" negative-alpha encoding.
	// The boost pushes low-opacity gaussians towards solid points while
	// still letting opacity-driven effects (dissolve, cutouts) fade them out.
	half alpha = saturate(abs(i.col.a) * _PointOpacityBoost) * coverage;
	if (alpha < 5.0/255.0)
		discard;

	// Premultiplied for the front-to-back under-blend; near-solid points
	// naturally occlude the ones sorted behind them.
	return half4(i.col.rgb * alpha, alpha);
}
ENDCG
        }
    }
}
