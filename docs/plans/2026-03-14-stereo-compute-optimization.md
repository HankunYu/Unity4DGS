# Stereo Compute Optimization: Single-Dispatch Dual-Eye CalcViewData

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Merge the two per-eye compute dispatches into a single dispatch that computes view-independent data once and per-eye data in an internal loop, cutting ~40% of stereo compute cost.

**Architecture:** The current stereo path dispatches `CSCalcViewData` twice (once per eye), repeating expensive view-independent work (splat loading, 3D covariance, SH evaluation). The optimization passes both eyes' matrices to a single dispatch. The kernel computes shared data once, then loops over eyes for clip position + 2D covariance + decomposition only.

**Tech Stack:** Unity Compute Shaders (HLSL), C# CommandBuffer API

---

## Analysis: View-Independent vs View-Dependent Work

| Work Item | Per-Eye? | Cost | Notes |
|-----------|----------|------|-------|
| Splat data load (pos/rot/scale/SH/opacity) | No | HIGH (memory bandwidth) | 2M splats × ~80 bytes |
| Morph/animation data load | No | MEDIUM | Only when active |
| World position (`mul(O2W, pos)`) | No | LOW | 1 mat mul |
| 3D covariance (`CalcCovariance3D`) | No | HIGH | Rotation quat → matrix → outer product |
| SH evaluation (`ShadeSH`) | No | HIGH | Up to SH order 3 = 16 coefficients |
| Deleted/cut/selection check | No | LOW | Bit operations |
| Clip position (`mul(VP, worldPos)`) | **Yes** | LOW | 1 mat mul per eye |
| Frustum cull | **Yes** | LOW | Comparisons |
| 2D covariance (`CalcCovariance2D`) | **Yes** | MEDIUM | Jacobian + matrix products |
| Covariance decompose | **Yes** | MEDIUM | Eigendecomposition |
| Color pack (f32tof16) | **Yes** | LOW | Bit ops |

**Estimated savings:** ~40-50% of stereo compute time (the view-independent work dominates).

---

## Task 1: Add Dual-Eye Matrix Uniforms to Compute Shader

**Files:**
- Modify: `Assets/4DGS/Resources/SplatUtilities.compute:35-48`

**Step 1:** Add per-eye matrix arrays alongside existing uniforms

Add these uniforms after the existing `_MatrixP` declaration:

```hlsl
// Stereo per-eye matrices for single-dispatch dual-eye path
float4x4 _MatrixVP_Eye[2];
float4x4 _MatrixP_Eye[2];
float4x4 _MatrixMV_Eye[2];
```

**Step 2:** Add C# property IDs

In `GaussianSplatRenderer.cs` Props class, add:

```csharp
public static readonly int MatrixVPEye = Shader.PropertyToID("_MatrixVP_Eye");
public static readonly int MatrixPEye = Shader.PropertyToID("_MatrixP_Eye");
public static readonly int MatrixMVEye = Shader.PropertyToID("_MatrixMV_Eye");
```

Note: Unity's `SetComputeMatrixArrayParam` can set float4x4 arrays.

**Step 3:** Commit

---

## Task 2: Create Dual-Eye Kernel `CSCalcViewDataStereo`

**Files:**
- Modify: `Assets/4DGS/Resources/SplatUtilities.compute`
- Modify: `Assets/4DGS/Shaders/GaussianSplatting.hlsl` (extract view-independent helper)

**Step 1:** Extract view-independent intermediate struct

In `SplatUtilities.compute`, add before `CalculateEyeViewData`:

```hlsl
struct SplatSharedData
{
    float3 worldPos;
    float3 cov3d0;
    float3 cov3d1;
    float splatScale2;
    half4 color;       // SH-evaluated color + opacity
    bool skip;         // deleted, cut, or zero opacity
};
```

**Step 2:** Extract view-independent computation into helper

```hlsl
SplatSharedData CalcSharedSplatData(
    SplatData splat, float3 centerWorldPos,
    float splatScale, half opacityScale,
    float animOpacityMul, float animScaleMul,
    float animColorBlend, float3 animColorTint)
{
    SplatSharedData shared = (SplatSharedData)0;
    shared.worldPos = centerWorldPos;

    float finalOpacity = splat.opacity * opacityScale * animOpacityMul;
    if (finalOpacity < 1.0 / 255.0) { shared.skip = true; return shared; }

    float4 boxRot = splat.rot;
    float3 boxSize = splat.scale * animScaleMul;
    float3x3 splatRotScaleMat = CalcMatrixFromRotationScale(boxRot, boxSize);

    CalcCovariance3D(splatRotScaleMat, shared.cov3d0, shared.cov3d1);
    shared.splatScale2 = splatScale * splatScale;
    shared.cov3d0 *= shared.splatScale2;
    shared.cov3d1 *= shared.splatScale2;

    float3 worldViewDir = _VecWorldSpaceCameraPos.xyz - centerWorldPos;
    float3 objViewDir = mul((float3x3)_MatrixWorldToObject, worldViewDir);
    objViewDir = normalize(objViewDir);

    shared.color.rgb = ShadeSH(splat.sh, objViewDir, _SHOrder, _SHOnly != 0);
    if (animColorBlend > 0)
        shared.color.rgb = lerp(shared.color.rgb, (half3)animColorTint, (half)animColorBlend);
    shared.color.a = min(finalOpacity, 65000);

    return shared;
}
```

**Step 3:** Create per-eye projection helper (lightweight)

```hlsl
SplatViewData CalcPerEyeViewData(
    SplatData splat, SplatSharedData shared,
    float4x4 vpMatrix, float4x4 pMatrix, float4x4 mvMatrix,
    bool isDeleted, bool isCut, float splatScale)
{
    SplatViewData view = (SplatViewData)0;
    if (shared.skip) return view;

    float4 clipPos = mul(vpMatrix, float4(shared.worldPos, 1));
    if (isDeleted || isCut) clipPos.w = 0;

    if (clipPos.w > 0)
    {
        float maxScale = max(max(abs(splat.scale.x), abs(splat.scale.y)), abs(splat.scale.z)) * splatScale;
        float clipMargin = maxScale * abs(pMatrix._m00);
        float w = clipPos.w;
        bool outside = (clipPos.x < -(w+clipMargin)) || (clipPos.x > (w+clipMargin)) ||
                       (clipPos.y < -(w+clipMargin)) || (clipPos.y > (w+clipMargin));
        if (outside) clipPos.w = 0;
    }
    view.pos = clipPos;

    if (clipPos.w > 0)
    {
        float3 cov2d = CalcCovariance2D(splat.pos, shared.cov3d0, shared.cov3d1,
                                         mvMatrix, pMatrix, _VecScreenParams);
        DecomposeCovariance(cov2d, view.axis1, view.axis2);

        float splatArea = length(view.axis1) * length(view.axis2);
        if (splatArea < 1.0) return view;

        view.color.x = (f32tof16(shared.color.r) << 16) | f32tof16(shared.color.g);
        view.color.y = (f32tof16(shared.color.b) << 16) | f32tof16(shared.color.a);
    }
    return view;
}
```

**Step 4:** Create the stereo kernel

```hlsl
[numthreads(GROUP_SIZE,1,1)]
void CSCalcViewDataStereo (uint3 id : SV_DispatchThreadID, uint3 gtid : SV_GroupThreadID)
{
    // Same chunk culling as CSCalcViewData, but use eye 0's VP (conservative)
    // ... (copy chunk cull logic, using _MatrixVP_Eye[0])

    if (idx >= _SplatCount) return;
    if (_chunkVisible[chunkSlot] == 0)
    {
        _SplatViewData[idx * 2 + 0] = (SplatViewData)0;
        _SplatViewData[idx * 2 + 1] = (SplatViewData)0;
        return;
    }

    // Load splat data ONCE
    SplatData splat = LoadSplatData(idx);
    float3 centerWorldPos = mul(_MatrixObjectToWorld, float4(splat.pos,1)).xyz;

    // Compute view-independent data ONCE
    SplatSharedData shared = CalcSharedSplatData(splat, centerWorldPos, ...);

    // Compute per-eye data (lightweight)
    [unroll]
    for (uint eye = 0; eye < 2; eye++)
    {
        SplatViewData view = CalcPerEyeViewData(
            splat, shared,
            _MatrixVP_Eye[eye], _MatrixP_Eye[eye], _MatrixMV_Eye[eye],
            isDeleted, isCut, _SplatScale);

        // Selection highlight
        if (isSelected && view.pos.w > 0) { /* negate alpha */ }

        _SplatViewData[idx * 2 + eye] = view;
    }
}
```

Add the kernel pragma at the top of the file:
```hlsl
#pragma kernel CSCalcViewDataStereo
```

**Step 5:** Commit

---

## Task 3: Update C# to Use Single Stereo Dispatch

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs:658-681`

**Step 1:** Add kernel name constant and cache

```csharp
private const string CalcViewDataStereoKernelName = "CSCalcViewDataStereo";
```

In `KernelIndices` enum (or similar), add the stereo kernel lookup.

**Step 2:** Replace the per-eye dispatch loop with single dispatch

```csharp
if (isStereo)
{
    if (!TryFindSupportedKernel(CalcViewDataStereoKernelName, out int stereoKernel))
    {
        // Fallback to per-eye dispatch if stereo kernel not available
        // ... (keep existing per-eye loop as fallback)
    }
    else
    {
        cmb.SetComputeIntParam(csSplatUtilities, Props.IsStereo, 1);

        // Pass both eyes' matrices at once
        var leftView = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
        var rightView = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
        var leftProj = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
        var rightProj = cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right);
        var leftGpu = GL.GetGPUProjectionMatrix(leftProj, true);
        var rightGpu = GL.GetGPUProjectionMatrix(rightProj, true);

        // Set matrix arrays (2 elements each)
        cmb.SetComputeMatrixArrayParam(csSplatUtilities, Props.MatrixVPEye,
            new[] { leftGpu * leftView, rightGpu * rightView });
        cmb.SetComputeMatrixArrayParam(csSplatUtilities, Props.MatrixPEye,
            new[] { leftGpu, rightGpu });
        cmb.SetComputeMatrixArrayParam(csSplatUtilities, Props.MatrixMVEye,
            new[] { leftView * matO2W, rightView * matO2W });

        csSplatUtilities.GetKernelThreadGroupSizes(stereoKernel, out uint gsX2, out _, out _);
        int groupCount2 = (EffectiveSplatCount + (int)gsX2 - 1) / (int)gsX2;
        cmb.DispatchCompute(csSplatUtilities, stereoKernel, groupCount2, 1, 1);
    }
}
```

**Step 3:** Commit

---

## Task 4: Verify and Clean Up

**Step 1:** Build for visionOS, verify rendering is identical to dual-dispatch path

**Step 2:** Profile: compare frame times between old (2 dispatches) and new (1 dispatch)

**Step 3:** Remove the per-eye loop fallback if stereo kernel works reliably

**Step 4:** Commit

---

## Implementation Notes

- The `_VecWorldSpaceCameraPos` uses the mono camera position for SH evaluation in both eyes. The IPD offset (~63mm) is negligible for SH at typical scene distances (>1m). No change needed.
- Chunk frustum culling uses eye 0's VP matrix. This is conservative — a chunk visible to eye 1 but not eye 0 would still be rendered (the per-splat frustum cull catches it). The IPD offset is tiny relative to chunk AABB sizes.
- The `[unroll]` on the eye loop ensures no branching overhead — the compiler unrolls both iterations.
- Non-stereo path (`CSCalcViewData`) is unchanged — no regression risk for editor/desktop.
