# Vision Pro Stereo Rendering Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Enable Gaussian Splatting rendering on Apple Vision Pro using Single Pass Instanced stereo rendering.

**Architecture:** Follow the approach proven on Quest 3 (upstream PR #173): compute shader calculates view data for both eyes in a single dispatch, `_SplatViewData` buffer is doubled (left at `[idx*2]`, right at `[idx*2+1]`), and the main render shader selects per-eye data via `_EyeIndex`. URP feature manually renders per eye to texture array slices, bypassing Unity's SPI instance multiplier. Composite shader samples from `Texture2DArray`.

**Tech Stack:** Unity 6 URP, RenderGraph (UnsafePass), ComputeShader, HLSL, C#

**Reference:** [aras-p/UnityGaussianSplatting PR #173](https://github.com/aras-p/UnityGaussianSplatting/pull/173)

---

### Task 1: Clean up existing VR code

Remove the current VR implementation that took a different approach (vertex-shader covariance). We're starting fresh with the compute-prepass approach from PR #173.

**Files:**
- Delete: `Assets/4DGS/Shaders/RenderGaussianSplatsVR.shader`
- Delete: `Assets/4DGS/Shaders/RenderGaussianSplatsVR.shader.meta`
- Delete: `Assets/4DGS/Runtime/GaussianSplatVRMesh.cs`
- Delete: `Assets/4DGS/Runtime/GaussianSplatVRMesh.cs.meta`
- Modify: `Assets/4DGS/Runtime/GaussianSplatConfig.cs`
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs`
- Modify: `Assets/4DGS/Shaders/GaussianSplatting.hlsl`

**Step 1: Delete VR-specific files**

Delete the four files listed above (shader, mesh generator, and their .meta files).

**Step 2: Clean GaussianSplatConfig.cs**

Remove VR-specific fields. The file should become:

```csharp
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    [ExecuteInEditMode]
    public class GaussianSplatConfig : MonoBehaviour
    {
        [Header("Global Render Settings")]
        public GaussianSplatRenderMode renderMode = GaussianSplatRenderMode.Splats;
        [Range(1.0f, 15.0f)] public float pointDisplaySize = 3.0f;
        public bool useTileRenderer = true;

        // Auto-loaded resources (not serialized)
        private Shader _shaderSplats;
        private Shader _shaderComposite;
        private Shader _shaderDebugPoints;
        private Shader _shaderDebugBoxes;
        private ComputeShader _csSplatUtilities;
        private ComputeShader _csTileRender;

        public Shader ShaderSplats => _shaderSplats;
        public Shader ShaderComposite => _shaderComposite;
        public Shader ShaderDebugPoints => _shaderDebugPoints;
        public Shader ShaderDebugBoxes => _shaderDebugBoxes;
        public ComputeShader CsSplatUtilities => _csSplatUtilities;
        public ComputeShader CsTileRender => _csTileRender;

        public bool ResourcesValid =>
            _shaderSplats != null && _shaderComposite != null &&
            _shaderDebugPoints != null && _shaderDebugBoxes != null &&
            _csSplatUtilities != null && SystemInfo.supportsComputeShaders;

        private void OnEnable()
        {
            LoadResources();
        }

        private void LoadResources()
        {
            _shaderSplats = Shader.Find("Gaussian Splatting/Render Splats");
            _shaderComposite = Shader.Find("Hidden/Gaussian Splatting/Composite");
            _shaderDebugPoints = Shader.Find("Gaussian Splatting/Debug/Render Points");
            _shaderDebugBoxes = Shader.Find("Gaussian Splatting/Debug/Render Boxes");
            _csSplatUtilities = Resources.Load<ComputeShader>("SplatUtilities");
            _csTileRender = Resources.Load<ComputeShader>("GaussianTileRender");
        }
    }
}
```

**Step 3: Clean GaussianSplatRenderSystem.cs**

Remove all VR-specific code:
- Remove fields: `_vrMaterial`, `_vrMpb` (lines 67-68)
- Remove properties: `ActiveSplatCount`, `HasVRMaterial` (lines 75-77)
- Remove VR material creation in `EnsureMaterials()` (lines 94-97)
- Remove VR material destruction in `DisposeGlobalResources()` (lines 196, 201)
- Remove VR check in `CanUseGlobalSortPath()` (lines 310-312)
- Remove VR branch in `SortAndRenderSplatsPerObject()` (lines 547, 561-565)
- Remove methods: `SortVRSplats()`, `RenderVRSplats()`, `SetVRMaterialProperties()`, `RenderSingleSplatVR()` (lines 614-699)

**Step 4: Clean GaussianSplatting.hlsl**

Remove the `GAUSSIAN_SPLAT_SKIP_BUFFER_DECLARATIONS` guard around `SplatIndexToPixelIndex` (lines 181-196). Restore to:

```hlsl
static const uint kTexWidth = 2048;

uint3 SplatIndexToPixelIndex(uint idx)
{
    uint3 res;
    uint2 xy = DecodeMorton2D_16x16(idx);
    uint width = kTexWidth / 16;
    idx >>= 8;
    res.x = (idx % width) * 16 + xy.x;
    res.y = (idx / width) * 16 + xy.y;
    res.z = 0;
    return res;
}
```

**Step 5: Remove VR path from GaussianSplatURPFeature.cs**

Remove lines 115-165 (the entire `vrPath` branch in `RecordRenderGraph`). This will be re-implemented properly in Task 5.

**Step 6: Commit**

```
git add -A && git commit -m "refactor: remove previous VR implementation for clean stereo restart"
```

---

### Task 2: Add stereo view data to compute shader

Modify `SplatUtilities.compute` to calculate view data for both eyes in a single dispatch when stereo is active. This is the core of the stereo approach.

**Files:**
- Modify: `Assets/4DGS/Resources/SplatUtilities.compute`

**Step 1: Add stereo matrix uniforms**

After line 44 (`float4x4 _MatrixVP;`), add:

```hlsl
float4x4 _ViewProjMatrixLeft;
float4x4 _ViewProjMatrixRight;
uint _IsStereo;
```

**Step 2: Extract per-eye view data calculation into a helper function**

Before the `CSCalcViewData` kernel (line 224), add a new function that takes a VP matrix and returns `SplatViewData`. This function should contain the core logic currently inside `CSCalcViewData` (lines 355-475), parameterized by a `viewProjMatrix` argument instead of using `UNITY_MATRIX_VP` directly.

The function signature:

```hlsl
SplatViewData CalculateEyeViewData(
    SplatData splat,
    float3 centerWorldPos,
    float4x4 viewProjMatrix,
    bool isDeleted,
    bool isCut,
    float splatScale,
    half opacityScale,
    float animOpacityMul,
    float animScaleMul,
    float animColorBlend,
    float3 animColorTint)
```

The body is essentially the same as lines 376-475 of the current `CSCalcViewData`, but:
- Replace `mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1))` with `mul(viewProjMatrix, float4(centerWorldPos, 1))`
- Replace `abs(UNITY_MATRIX_P._m00)` with extracting `_m00` from the viewProjMatrix's projection component. Since we only have VP (not P separately), use the `_MatrixP` variable for the frustum cull approximation, or remove the per-splat conservative frustum cull for the stereo path (the chunk-level cull already handles this).
  - **Simplest approach**: Keep `UNITY_MATRIX_P._m00` as-is for the non-stereo helper call since `UNITY_MATRIX_P` is set by Unity. For stereo, we can pass `_MatrixP._m00` or skip the per-splat frustum cull (chunk cull handles most of it).
- Replace `_SplatViewData[idx] = view;` with `return view;`

**Step 3: Modify CSCalcViewData to call helper for both eyes**

The new `CSCalcViewData` kernel should:
1. Keep all existing setup code (chunk culling, splat loading, morph, animation, deleted/cut checks)
2. At the point where view data is computed (line 376+), branch on `_IsStereo`:

```hlsl
if (_IsStereo)
{
    SplatViewData viewLeft = CalculateEyeViewData(splat, centerWorldPos,
        _ViewProjMatrixLeft, isDeleted, isCut, splatScale, opacityScale,
        animOpacityMul, animScaleMul, animColorBlend, animColorTint);
    SplatViewData viewRight = CalculateEyeViewData(splat, centerWorldPos,
        _ViewProjMatrixRight, isDeleted, isCut, splatScale, opacityScale,
        animOpacityMul, animScaleMul, animColorBlend, animColorTint);
    _SplatViewData[idx * 2] = viewLeft;
    _SplatViewData[idx * 2 + 1] = viewRight;
}
else
{
    SplatViewData view = CalculateEyeViewData(splat, centerWorldPos,
        _MatrixVP, isDeleted, isCut, splatScale, opacityScale,
        animOpacityMul, animScaleMul, animColorBlend, animColorTint);
    _SplatViewData[idx] = view;
}
```

**Important**: The dispatch count in `CalcViewData()` C# side uses `_SplatCount` (not buffer size), so the dispatch stays the same. The buffer just has 2x capacity.

**Step 4: Commit**

```
git add Assets/4DGS/Resources/SplatUtilities.compute
git commit -m "feat: add stereo dual-eye view data calculation to compute shader"
```

---

### Task 3: Add stereo support to C# renderer

Double the `_SplatViewData` buffer, pass stereo matrices in `CalcViewData`, and add `_EyeIndex`/`_IsStereo` shader properties.

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

**Step 1: Add new shader property IDs**

In the `Props` class (around line 84-144), add:

```csharp
public static readonly int EyeIndex = Shader.PropertyToID("_EyeIndex");
public static readonly int IsStereo = Shader.PropertyToID("_IsStereo");
public static readonly int ViewProjMatrixLeft = Shader.PropertyToID("_ViewProjMatrixLeft");
public static readonly int ViewProjMatrixRight = Shader.PropertyToID("_ViewProjMatrixRight");
```

**Step 2: Double `m_GpuView` buffer size**

In `InitResourcesForAssets()` (line 284), change:
```csharp
// Before:
_gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)splatCountMaxSize, GpuViewDataSize);
// After:
_gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)splatCountMaxSize * 2, GpuViewDataSize);
```

In `InitResourcesForAsset()` (line 314), change:
```csharp
// Before:
_gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatAsset.splatCount, GpuViewDataSize);
// After:
_gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatAsset.splatCount * 2, GpuViewDataSize);
```

**Step 3: Pass stereo matrices in CalcViewData**

In `CalcViewData()` (line 602+), after setting `_MatrixWorldToObject` (line 623), add stereo matrix logic:

```csharp
bool isStereo = XRSettings.enabled && cam.stereoEnabled &&
                (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced ||
                 XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview) &&
                !Application.isEditor;

if (isStereo)
{
    Matrix4x4 stereoViewLeft = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
    Matrix4x4 stereoProjLeft = GL.GetGPUProjectionMatrix(
        cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), true);
    cmb.SetComputeMatrixParam(csSplatUtilities, Props.ViewProjMatrixLeft,
        stereoProjLeft * stereoViewLeft);

    Matrix4x4 stereoViewRight = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
    Matrix4x4 stereoProjRight = GL.GetGPUProjectionMatrix(
        cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), true);
    cmb.SetComputeMatrixParam(csSplatUtilities, Props.ViewProjMatrixRight,
        stereoProjRight * stereoViewRight);

    cmb.SetComputeIntParam(csSplatUtilities, Props.IsStereo, 1);
}
else
{
    cmb.SetComputeIntParam(csSplatUtilities, Props.IsStereo, 0);
}
```

Also fix the dispatch count — change line 634 from using `m_GpuView.count` to `_splatCount`:

```csharp
// Before (if using m_GpuView.count):
cmb.DispatchCompute(csSplatUtilities, kernelIndex, (m_GpuView.count + (int)gsX - 1)/(int)gsX, 1, 1);
// After:
int dispatchCount = EffectiveSplatCount;
cmb.DispatchCompute(csSplatUtilities, kernelIndex, (dispatchCount + (int)gsX - 1)/(int)gsX, 1, 1);
```

(Note: the current code already uses `EffectiveSplatCount` — verify this is correct after doubling the buffer.)

**Step 4: Commit**

```
git add Assets/4DGS/Runtime/GaussianSplatRenderer.cs
git commit -m "feat: add stereo buffer allocation and matrix passing"
```

---

### Task 4: Add eye index to render shader

Minimal changes to the main splat render shader to select per-eye view data.

**Files:**
- Modify: `Assets/4DGS/Shaders/RenderGaussianSplats.shader`

**Step 1: Add stereo uniforms and modify view data lookup**

After the `_SplatViewData` declaration (line 31), add:

```hlsl
uint _EyeIndex;
uint _IsStereo;
```

In the vertex shader (line 33+), change the view data lookup:

```hlsl
// Before (line 36-37):
instID = _OrderBuffer[instID];
SplatViewData view = _SplatViewData[instID];

// After:
instID = _OrderBuffer[instID];
uint viewIndex = _IsStereo ? instID * 2 + _EyeIndex : instID;
SplatViewData view = _SplatViewData[viewIndex];
```

**Step 2: Commit**

```
git add Assets/4DGS/Shaders/RenderGaussianSplats.shader
git commit -m "feat: add per-eye view data selection to render shader"
```

---

### Task 5: Add stereo rendering to URP feature

The URP feature needs to detect stereo, render to each texture array slice separately, and composite per-eye.

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs`
- Modify: `Assets/4DGS/Runtime/GaussianSplatURPFeature.cs`

**Step 1: Refactor SortAndRenderSplats into Prepare + Render**

In `GaussianSplatRenderSystem.cs`, we need to split the render system so we can:
1. Sort + CalcViewData once
2. Render multiple times (once per eye)

Add a struct and field to the render system:

```csharp
public struct StereoRenderItem
{
    public GaussianSplatRenderer gs;
    public Material displayMat;
    public MaterialPropertyBlock mpb;
    public int indexCount;
    public int instanceCount;
    public MeshTopology topology;
}

private readonly List<StereoRenderItem> _preparedItems = new();
```

Add `PrepareSplats()` method — this does sorting + CalcViewData but no drawing:

```csharp
public Material PrepareSplats(Camera cam, CommandBuffer cmb)
{
    if (_hasRenderFence)
        cmb.WaitOnAsyncGraphicsFence(_lastRenderFence);

    _preparedItems.Clear();
    Material matComposite = _matComposite;

    foreach (var kvp in _activeSplats)
    {
        var gs = kvp.Item1;
        var mpb = kvp.Item2;

        // Sort
        var matrix = gs.transform.localToWorldMatrix;
        if (gs.FrameCounter % gs.sortNthFrame == 0)
            gs.SortPoints(cmb, cam, matrix);
        ++gs.FrameCounter;

        // Prepare material
        mpb.Clear();
        Material displayMat = _config.renderMode switch
        {
            GaussianSplatRenderMode.DebugPoints => _matDebugPoints,
            GaussianSplatRenderMode.DebugPointIndices => _matDebugPoints,
            GaussianSplatRenderMode.DebugBoxes => _matDebugBoxes,
            GaussianSplatRenderMode.DebugChunkBounds => _matDebugBoxes,
            _ => _matSplats
        };
        if (displayMat == null) continue;

        gs.SetAssetDataOnMaterial(mpb);
        mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.GpuChunksBuffer);
        mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.GpuView);
        mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.GpuSortKeys);
        mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.splatScale);
        mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.opacityScale);
        mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, _config.pointDisplaySize);
        mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.shOrder);
        mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.shOnly ? 1 : 0);
        mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex,
            _config.renderMode == GaussianSplatRenderMode.DebugPointIndices ? 1 : 0);
        mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks,
            _config.renderMode == GaussianSplatRenderMode.DebugChunkBounds ? 1 : 0);

        cmb.BeginSample(ProfCalcView);
        bool calcSuccess = gs.CalcViewData(cmb, cam);
        cmb.EndSample(ProfCalcView);
        if (!calcSuccess) continue;

        int indexCount = 6;
        int instanceCount = gs.EffectiveSplatCount;
        MeshTopology topology = MeshTopology.Triangles;
        if (_config.renderMode is GaussianSplatRenderMode.DebugBoxes or GaussianSplatRenderMode.DebugChunkBounds)
            indexCount = 36;
        if (_config.renderMode == GaussianSplatRenderMode.DebugChunkBounds)
            instanceCount = gs.GpuChunksValid ? gs.GpuChunksBuffer.count : 0;

        _preparedItems.Add(new StereoRenderItem
        {
            gs = gs, displayMat = displayMat, mpb = mpb,
            indexCount = indexCount, instanceCount = instanceCount, topology = topology
        });
    }

    _lastRenderFence = cmb.CreateAsyncGraphicsFence();
    _hasRenderFence = true;
    return matComposite;
}
```

Add `RenderPreparedSplats()` method — draws all prepared items for a specific eye:

```csharp
public void RenderPreparedSplats(CommandBuffer cmb, int eyeIndex)
{
    foreach (var item in _preparedItems)
    {
        item.mpb.SetInteger(GaussianSplatRenderer.Props.EyeIndex, eyeIndex);
        item.mpb.SetInteger(GaussianSplatRenderer.Props.IsStereo, eyeIndex >= 0 ? 1 : 0);

        cmb.BeginSample(ProfDraw);
        cmb.DrawProcedural(item.gs.GpuIndexBuffer, item.gs.transform.localToWorldMatrix,
            item.displayMat, 0, item.topology, item.indexCount, item.instanceCount, item.mpb);
        cmb.EndSample(ProfDraw);
    }
}
```

**Step 2: Add stereo rendering to GaussianSplatURPFeature**

In `RecordRenderGraph()`, add stereo detection and per-eye rendering. The full new implementation of the `GsRenderPass.RecordRenderGraph` method:

```csharp
public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
{
    var cameraData = frameData.Get<UniversalCameraData>();
    var resourceData = frameData.Get<UniversalResourceData>();

    bool isStereo = XRSettings.enabled && cameraData.camera.stereoEnabled &&
                    (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced ||
                     XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview) &&
                    !Application.isEditor &&
                    cameraData.cameraTargetDescriptor.dimension == TextureDimension.Tex2DArray;

    using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

    RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
    rtDesc.depthBufferBits = 0;
    rtDesc.msaaSamples = 1;
    rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
    rtDesc.enableRandomWrite = true;
    TextureHandle gaussianSplatRT = UniversalRenderer.CreateRenderGraphTexture(
        renderGraph, rtDesc, GaussianSplatRTName, true);

    // ... (keep existing stylize logic for non-stereo) ...

    passData.CameraData = cameraData;
    passData.SourceTexture = resourceData.activeColorTexture;
    passData.SourceDepth = resourceData.activeDepthTexture;
    passData.GaussianSplatRT = gaussianSplatRT;
    passData.IsStereo = isStereo;

    builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
    builder.UseTexture(resourceData.activeDepthTexture);
    builder.UseTexture(gaussianSplatRT, AccessFlags.ReadWrite);
    builder.AllowPassCulling(false);

    builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
    {
        var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
        using var _ = new ProfilingScope(commandBuffer, ProfileSampler);
        var system = GaussianSplatRenderSystem.instance;

        if (data.IsStereo)
        {
            // Prepare once (sort + compute view data for both eyes)
            Material matComposite = system.PrepareSplats(data.CameraData.camera, commandBuffer);

            // Clear entire RT
            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT,
                ClearFlag.Color, Color.clear);

            // Render left eye to slice 0
            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT,
                ClearFlag.Color, Color.clear, 0, CubemapFace.Unknown, 0);
            system.RenderPreparedSplats(commandBuffer, 0);

            // Render right eye to slice 1
            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT,
                ClearFlag.Color, Color.clear, 0, CubemapFace.Unknown, 1);
            system.RenderPreparedSplats(commandBuffer, 1);

            // Composite per eye
            if (matComposite != null)
            {
                commandBuffer.BeginSample(GaussianSplatRenderSystem.ProfCompose);
                matComposite.SetTexture(GaussianSplatRT, data.GaussianSplatRT);

                commandBuffer.SetRenderTarget(data.SourceTexture, 0, CubemapFace.Unknown, 0);
                commandBuffer.SetGlobalInt("_CustomStereoEyeIndex", 0);
                commandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0,
                    MeshTopology.Triangles, 3, 1);

                commandBuffer.SetRenderTarget(data.SourceTexture, 0, CubemapFace.Unknown, 1);
                commandBuffer.SetGlobalInt("_CustomStereoEyeIndex", 1);
                commandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0,
                    MeshTopology.Triangles, 3, 1);
                commandBuffer.EndSample(GaussianSplatRenderSystem.ProfCompose);
            }
        }
        else
        {
            // Non-stereo path (existing logic)
            commandBuffer.SetGlobalTexture(GaussianSplatRT, data.GaussianSplatRT);
            CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT,
                data.SourceDepth, ClearFlag.Color, Color.clear);
            system.TileOutputTarget = data.GaussianSplatRT;
            system.TileRenderSize = data.RenderSize;
            Material matComposite = system.SortAndRenderSplats(
                data.CameraData.camera, commandBuffer);

            if (matComposite != null)
            {
                // ... (keep existing stylize + composite logic) ...
            }
        }
    });
}
```

**Note:** Add `bool IsStereo;` to the `PassData` class. Also add `using UnityEngine.XR;` and `using UnityEngine.Rendering;` (for `TextureDimension`) to the file's imports.

**Step 3: Commit**

```
git add Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs Assets/4DGS/Runtime/GaussianSplatURPFeature.cs
git commit -m "feat: add stereo prepare/render split and URP stereo path"
```

---

### Task 6: Add stereo support to composite shader

The composite shader needs to sample from `Texture2DArray` when in stereo mode, using `_CustomStereoEyeIndex` to pick the correct slice.

**Files:**
- Modify: `Assets/4DGS/Shaders/GaussianComposite.shader`

**Step 1: Modify composite shader for Texture2DArray**

Replace the current shader content with:

```hlsl
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
#pragma use_dxc
#pragma require 2darray
#pragma multi_compile_local _ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON

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

#if defined(UNITY_SINGLE_PASS_STEREO) || defined(STEREO_INSTANCING_ON) || defined(STEREO_MULTIVIEW_ON)
UNITY_DECLARE_TEX2DARRAY(_GaussianSplatRT);
#else
Texture2D _GaussianSplatRT;
#endif

int _CustomStereoEyeIndex;

half4 frag (v2f i) : SV_Target
{
    half4 col;
    #if defined(UNITY_SINGLE_PASS_STEREO) || defined(STEREO_INSTANCING_ON) || defined(STEREO_MULTIVIEW_ON)
        float2 normalizedUV = float2(i.vertex.x / _ScreenParams.x, i.vertex.y / _ScreenParams.y);
        col = UNITY_SAMPLE_TEX2DARRAY(_GaussianSplatRT, float3(normalizedUV, _CustomStereoEyeIndex));
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
```

**Key changes:**
- Added `#pragma require 2darray`
- Added `#pragma multi_compile_local _ UNITY_SINGLE_PASS_STEREO STEREO_INSTANCING_ON STEREO_MULTIVIEW_ON`
- Conditional `UNITY_DECLARE_TEX2DARRAY` vs `Texture2D`
- Fragment shader samples correct array slice via `_CustomStereoEyeIndex`
- Removed the old stereo undef hack (lines 19-26) since we now properly handle stereo

**Step 2: Commit**

```
git add Assets/4DGS/Shaders/GaussianComposite.shader
git commit -m "feat: add Texture2DArray stereo support to composite shader"
```

---

### Task 7: Verify compilation and non-stereo regression

Ensure all changes compile and the non-stereo (desktop) path still works correctly.

**Step 1: Unity compilation check**

Open Unity and verify no compilation errors. Check Console for any shader errors.

If headless:
```bash
UNITY=/Applications/Unity/Hub/Editor/6000.2.6f2/Unity.app/Contents/MacOS/Unity
$UNITY -projectPath "$(pwd)" -batchmode -quit -logFile Logs/import.log
```

**Step 2: Non-stereo regression test**

1. Open a scene with a `GaussianSplatRenderer` and `GaussianSplatConfig`
2. Verify splats render correctly in the Scene/Game view
3. Verify the `_IsStereo` / `_EyeIndex` defaults (0) don't break existing rendering
4. The non-stereo path should use `_SplatViewData[instID]` (since `_IsStereo = 0`)

**Step 3: Verify GpuView buffer**

Check that doubling the buffer doesn't cause issues:
- `m_GpuView.count` is now `splatCount * 2`
- The dispatch count in `CalcViewData` still uses `_SplatCount` (not buffer count)
- Non-stereo writes to `_SplatViewData[idx]` — the extra capacity is unused but harmless

**Step 4: Commit any fixes**

```
git commit -m "fix: resolve compilation issues from stereo changes"
```

---

### Notes for Vision Pro Testing

Once the code compiles and non-stereo works:

1. **Build for visionOS**: Target visionOS in Build Settings, ensure XR plugin is configured
2. **Verify stereo detection**: The `isStereo` flag in URP feature should be `true` on device
3. **Check `cam.GetStereoViewMatrix()`**: Returns correct per-eye matrices on visionOS
4. **Monitor performance**: Two DrawProcedural calls + doubled compute work
5. **Potential issues**:
   - `Application.isEditor` guard in stereo detection — may need adjustment for Play Mode XR testing
   - `cameraTargetDescriptor.dimension == TextureDimension.Tex2DArray` — verify this is correct on visionOS
   - Composite shader `_ScreenParams` may differ per eye — verify UV calculation
   - Chunk frustum culling uses center-eye `_MatrixVP` — may cull visible chunks at screen edges for one eye
