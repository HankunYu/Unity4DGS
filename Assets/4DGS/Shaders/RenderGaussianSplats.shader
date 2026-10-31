// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Render Splats"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        // Pass 0: offscreen. Front-to-back under-blend into the splat RT, in
        // linear space like the direct pass; the composite blends the
        // premultiplied result over the scene.
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

#include "RenderGaussianSplatsPass.hlsl"

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
	return SplatVert(vtxID, instID);
}

half4 frag (v2f i) : SV_Target
{
	return SplatColor(i);
}
ENDCG
        }

        // Pass 1: direct. Back-to-front premultiplied over-blend straight into
        // the camera target, depth-tested against the scene. No intermediate
        // RT and no composite pass.
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend One OneMinusSrcAlpha
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

#include "RenderGaussianSplatsPass.hlsl"

v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
	return SplatVert(vtxID, instID);
}

half4 frag (v2f i) : SV_Target
{
	return SplatColor(i);
}
ENDCG
        }
    }
}
