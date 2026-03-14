// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
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
		quadPos *= 2;

		o.pos = quadPos;

		float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _VecScreenParams.xy;
		o.vertex = centerClipPos;
		o.vertex.xy += deltaScreenPos * centerClipPos.w;

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

	float power = -dot(i.pos, i.pos);
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

    if (alpha < 1.0/255.0)
        discard;

    half4 res = half4(i.col.rgb * alpha, alpha);
    return res;
}
ENDCG
        }
    }
}
