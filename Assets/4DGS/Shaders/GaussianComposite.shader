// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma require 2darray
// Use custom keyword instead of Unity stereo instancing macros.
// Unity's STEREO_INSTANCING_ON pulls in UnityInstancing.cginc which assigns to
// const-qualified unity_StereoEyeIndex — a Metal compiler error.
// NOTE: Must be global (not _local) so commandBuffer.EnableShaderKeyword works
// in the render graph — Material.EnableKeyword is CPU-immediate and gets
// disabled before the GPU executes the deferred draw commands.
#pragma multi_compile _ GAUSSIAN_STEREO

#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
    o.vertex = float4(quadPos, 1, 1);
    return o;
}

#if defined(GAUSSIAN_STEREO)
Texture2DArray _GaussianSplatRT;
#else
Texture2D _GaussianSplatRT;
#endif

float4 _VecScreenParams;
int _CustomStereoEyeIndex;

// DEBUG: set to 1 to show UV gradient instead of splats (viewport mapping test)
#define DEBUG_COMPOSITE_UV 0

half4 frag (v2f i) : SV_Target
{
    #if DEBUG_COMPOSITE_UV
        // Visual diagnostic: red/green gradient across full viewport.
        // If this gradient fills the entire VR view, composite mapping is correct.
        // If it only fills a portion, there's a viewport/scissor mismatch.
        half2 uv = i.vertex.xy / _VecScreenParams.xy;
        return half4(uv.x, uv.y, 0.2, 1);
    #endif

    half4 col;
    #if defined(GAUSSIAN_STEREO)
        col = _GaussianSplatRT.Load(int4(i.vertex.xy, _CustomStereoEyeIndex, 0));
    #else
        col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    #endif
    col.rgb = GammaToLinearSpace(col.rgb);
    col.a = saturate(col.a * 1.5);
    return col;
}
ENDCG
        }
    }
}
