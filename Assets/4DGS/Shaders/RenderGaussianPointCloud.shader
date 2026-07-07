// SPDX-License-Identifier: MIT
// Lightweight point cloud render mode. Reads the same sorted view data as the
// splat shader (_OrderBuffer -> _SplatViewData), so all animation, morph,
// modifier and cutout effects baked by CSCalcViewData are preserved. Draws
// round points sized by each splat's projected footprint (clamped + scaled).
Shader "Gaussian Splatting/Render Point Cloud"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            ZWrite Off
            Blend OneMinusDstAlpha One
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

// Enable foveated rendering (VRR) support on visionOS Metal.
#include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"

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

	// Round point mask
	if (dot(i.pos, i.pos) > 1.0)
		discard;

	// abs() ignores the editor "selected splat" negative-alpha encoding.
	// The boost pushes low-opacity gaussians towards solid points while
	// still letting opacity-driven effects (dissolve, cutouts) fade them out.
	half alpha = saturate(abs(i.col.a) * _PointOpacityBoost);
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
