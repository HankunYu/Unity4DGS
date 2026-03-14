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

// DEBUG: set to 1 to render a colored dot grid covering NDC [-1,1].
// Dots at the edges of the VR view = clip-to-viewport mapping is correct.
// Dots only in center = viewport is larger than expected.
#define DEBUG_RENDER_GRID 0

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o = (v2f)0;

    #if DEBUG_RENDER_GRID
    {
        // Arrange first 10000 splats in a 100x100 grid covering full NDC
        uint gridIdx = instID;
        if (gridIdx >= 10000) { o.vertex = asfloat(0x7fc00000); return o; }
        float2 gridUV = float2(gridIdx % 100, gridIdx / 100) / 99.0; // [0,1]
        float2 ndc = gridUV * 2.0 - 1.0; // [-1,1]
        uint idx = vtxID;
        float2 quadPos = float2(idx&1, (idx>>1)&1) * 2.0 - 1.0;
        o.pos = quadPos;
        // Each dot is 8 pixels radius
        float2 dotSize = 8.0 * 2.0 / _VecScreenParams.xy;
        o.vertex = float4(ndc + quadPos * dotSize, 0.5, 1.0);
        // Color: red=x, green=y, blue for border dots
        bool border = gridUV.x < 0.02 || gridUV.x > 0.98 || gridUV.y < 0.02 || gridUV.y > 0.98;
        o.col = half4(gridUV.x, gridUV.y, border ? 1.0 : 0.0, 1.0);
        return o;
    }
    #endif

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
