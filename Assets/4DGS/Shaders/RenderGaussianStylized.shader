// SPDX-License-Identifier: MIT
// Quad shader: draws the quads CSCalcViewData baked into the view data. Every
// splat in PointCloud mode goes through it, styled or not, and so do styled
// splats in Splats mode; plain splats sharing a global draw with them fall
// back to the splat pass.
Shader "Gaussian Splatting/Render Stylized"
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

        // Pass 0: offscreen. Front-to-back linear under-blend into the splat
        // RT, as in RenderGaussianSplats.shader, or the AOV resolve.
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

#include "RenderGaussianStylizedPass.hlsl"

v2fStylized vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
	return StylizedVert(vtxID, instID);
}

half4 frag (v2fStylized i) : SV_Target
{
	return StylizedColor(i);
}
ENDCG
        }

        // Pass 1: direct. Back-to-front premultiplied over-blend straight into
        // the camera target, as in RenderGaussianSplats.shader. Never used for
        // AOVs, which need the offscreen max-blend resolve.
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
// Direct stereo under foveated rendering: both eyes drawn into the whole eye
// texture array, each routed to its slice by the vertex stage.
#pragma multi_compile _ GAUSSIAN_LAYERED_EYES

// Enable foveated rendering (VRR) support on visionOS Metal.
#include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"

#include "RenderGaussianStylizedPass.hlsl"

v2fStylized vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
	return StylizedVert(vtxID, instID);
}

half4 frag (v2fStylized i) : SV_Target
{
	return StylizedColor(i);
}
ENDCG
        }
    }
}
