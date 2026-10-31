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

        // Pass 0: offscreen under-blend into the splat RT (and the AOV variants).
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

#include "RenderGaussianPointCloudPass.hlsl"

half4 frag (v2f i) : SV_Target
{
	return PointColor(i);
}
ENDCG
        }

        // Pass 1: direct. Back-to-front premultiplied over-blend straight into
        // the camera target, depth-tested against the scene. Never used for
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

// Enable foveated rendering (VRR) support on visionOS Metal.
#include_with_pragmas "Packages/com.unity.render-pipelines.core/ShaderLibrary/FoveatedRenderingKeywords.hlsl"

#include "RenderGaussianPointCloudPass.hlsl"

half4 frag (v2f i) : SV_Target
{
	return PointColor(i);
}
ENDCG
        }
    }
}
