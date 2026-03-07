# Split GaussianSplatRenderer Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Split the monolithic `GaussianSplatRenderer.cs` (2075 lines) into 4 focused files with clean API boundaries.

**Architecture:** Extract `GaussianSplatRenderSystem` into its own file, pull tile rendering into `GaussianTileRenderer`, and move all edit operations into `GaussianSplatEditManager`. The remaining `GaussianSplatRenderer` keeps core responsibilities: asset resources, materials, sorting, and view data. Cross-class coupling is replaced with `internal` properties and explicit methods.

**Tech Stack:** Unity 6, C#, URP, Compute Shaders

---

## Source File

All code comes from: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

Current structure:
- Lines 18-847: `GaussianSplatRenderSystem` class + `GlobalOrderGroupCache` nested class
- Lines 850-2074: `GaussianSplatRenderer` class (MonoBehaviour)

## Task 1: Extract GaussianTileRenderer

The tile rendering logic is the most self-contained piece inside `GaussianSplatRenderSystem`. Extract it first.

**Files:**
- Create: `Assets/4DGS/Runtime/GaussianTileRenderer.cs`
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

**Step 1: Create `GaussianTileRenderer.cs`**

Extract these members from `GaussianSplatRenderSystem` into a new `GaussianTileRenderer` class:
- `TileProps` static class (lines 69-92)
- `GlobalOrderGroupCache` nested class (lines 100-157) — tile-related fields only
- `EnsureTileResources()` method (lines 383-445)
- `CalcTileShift()` method (lines 450-459)
- `DispatchTileRender()` method (lines 461-588)
- Tile-related fields: `_tileSorter`, `_tileCs`, `TileOutputTarget`, `TileRenderSize`, `_kernelClearTileData`, `_kernelTileAssign`, `_kernelBuildRanges`, `_kernelRenderTiles`
- `LastTilePairCount` property (lines 249-262)
- `s_CounterReadback` static field (line 381)

The new class should:
- Be `internal` (not public, only used by RenderSystem)
- Accept `GlobalOrderGroupCache` from RenderSystem (keep cache in RenderSystem since it's shared between tile and non-tile paths)
- Have a `Dispatch(cmb, cam, cache, splatCount, sortNeeded, screenW, screenH)` method
- Have an `EnsureResources(reference, cache, screenW, screenH)` method
- Own the `TileOutputTarget` and `TileRenderSize` properties
- Expose profiler markers as `internal static readonly`

Tile-related fields to move OUT of `GlobalOrderGroupCache` into tile-specific management:
- Keep tile buffers in `GlobalOrderGroupCache` (they're part of the per-group state)
- Move tile compute shader references and kernel indices to `GaussianTileRenderer`

**Step 2: Update `GaussianSplatRenderSystem`**

In `SortAndRenderSplatsGlobal()`:
- Replace inline tile logic with `_tileRenderer.Dispatch(...)` call
- Replace `EnsureTileResources(...)` with `_tileRenderer.EnsureResources(...)`
- Add `_tileRenderer` field, initialize lazily

In `DisposeGlobalResources()`:
- Add `_tileRenderer = null`

Move `TileOutputTarget` and `TileRenderSize` access through `_tileRenderer`.

**Step 3: Verify compilation**

Run: Open Unity, check for compilation errors in Console.
Expected: Zero errors, zero warnings related to GaussianSplatting namespace.

**Step 4: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianTileRenderer.cs Assets/4DGS/Runtime/GaussianTileRenderer.cs.meta Assets/4DGS/Runtime/GaussianSplatRenderer.cs
git commit -m "refactor: extract GaussianTileRenderer from RenderSystem"
```

---

## Task 2: Extract GaussianSplatRenderSystem into its own file

**Files:**
- Create: `Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs`
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

**Step 1: Create `GaussianSplatRenderSystem.cs`**

Move the entire `GaussianSplatRenderSystem` class (now slimmed after Task 1) into its own file. This includes:
- Profiler markers
- Singleton pattern (`instance`, `_instance`)
- `_splats`, `_cameraCommandBuffersDone`, `_activeSplats`, `_globalMpb`, `_globalGroups`
- `_commandBuffer`, `_globalSorter`, `_globalSorterShader`, `_globalFrameId`
- GPU sync fields (`_lastRenderFence`, `_hasRenderFence`)
- `_tileRenderer` field (from Task 1)
- `_lastRetiredCleanupFrame`
- `GlobalOrderGroupCache` nested class
- All methods: `RegisterSplat`, `UnregisterSplat`, `GatherSplatsForCamera`, `SortAndRenderSplats`, `CanUseGlobalSortPath`, `EnsureGlobalSorter`, `EnsureGlobalGroupCache`, `ReleaseUnusedGlobalGroups`, `SortAndRenderSplatsGlobal`, `SortAndRenderSplatsPerObject`, `InitialClearCmdBuffer`, `OnPreCullCamera`, `DisposeGlobalResources`, `DisposeBuffer`

Keep the same namespace: `GaussianSplatting.Runtime`.

**Step 2: Clean up `GaussianSplatRenderer.cs`**

After extraction, `GaussianSplatRenderer.cs` should only contain:
- The `GaussianSplatRenderer : MonoBehaviour` class
- Its `Props` static class
- All instance fields and methods belonging to the MonoBehaviour

Verify that `GaussianSplatRenderer` still has `internal` access to members needed by `GaussianSplatRenderSystem` (they're in the same assembly, so `internal` works).

**Step 3: Verify compilation**

Run: Open Unity, check Console.
Expected: Zero errors.

**Step 4: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs.meta Assets/4DGS/Runtime/GaussianSplatRenderer.cs
git commit -m "refactor: extract GaussianSplatRenderSystem into own file"
```

---

## Task 3: Extract GaussianSplatEditManager

**Files:**
- Create: `Assets/4DGS/Runtime/GaussianSplatEditManager.cs`
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

**Step 1: Create `GaussianSplatEditManager.cs`**

Extract all edit-related members from `GaussianSplatRenderer`:

Fields to move:
- `_gpuEditCutouts`
- `_gpuEditCountsBounds`
- `_gpuEditSelected`
- `_gpuEditDeleted`
- `_gpuEditSelectedMouseDown`
- `_gpuEditPosMouseDown`
- `_gpuEditOtherMouseDown`

Properties to move:
- `editModified`
- `editSelectedSplats`
- `editDeletedSplats`
- `editCutSplats`
- `editSelectedBounds`
- `GpuEditDeleted`

Methods to move:
- `UpdateEditCountsAndBounds()`
- `UpdateCutoutsBuffer()`
- `EnsureEditingBuffers()`
- `EditStoreSelectionMouseDown()`
- `EditStorePosMouseDown()`
- `EditStoreOtherMouseDown()`
- `EditUpdateSelection()`
- `EditTranslateSelection()`
- `EditRotateSelection()`
- `EditScaleSelection()`
- `EditDeleteSelected()`
- `EditSelectAll()`
- `EditDeselectAll()`
- `EditInvertSelection()`
- `EditExportData()`
- `EditSetSplatCount()`
- `EditCopySplatsInto()`
- `EditCopySplats()`
- Helper: `ClearGraphicsBuffer()`, `UnionGraphicsBuffers()`, `SortableUintToFloat()`

Design the class as:
```csharp
namespace GaussianSplatting.Runtime
{
    internal class GaussianSplatEditManager
    {
        private readonly GaussianSplatRenderer _renderer;

        public GaussianSplatEditManager(GaussianSplatRenderer renderer)
        {
            _renderer = renderer;
        }

        // All edit methods here, accessing renderer via internal properties
    }
}
```

The edit manager needs access to these renderer members (expose as `internal` if not already):
- `_gpuPosData`, `_gpuOtherData`, `_gpuSHData`, `_gpuColorData` (via internal properties)
- `_gpuView`, `_gpuSortKeys`, `_gpuSortDistances`
- `csSplatUtilities` (compute shader)
- `splatAsset` (for format info)
- `_splatCount` (read/write via internal property)
- `_gpuChunks`, `_gpuChunksValid`
- `cutouts` array
- `transform` (from MonoBehaviour)
- `TryFindSupportedKernel()` method
- `SetAssetDataOnCS()` method
- `EffectiveSplatCount`
- `HasValidAsset`, `HasValidRenderSetup`

**Step 2: Add edit manager to `GaussianSplatRenderer`**

```csharp
// In GaussianSplatRenderer
private GaussianSplatEditManager _editManager;

internal GaussianSplatEditManager EditManager
    => _editManager ??= new GaussianSplatEditManager(this);
```

Add forwarding properties for backward compatibility with Editor code:
```csharp
public bool editModified => EditManager.Modified;
public uint editSelectedSplats => EditManager.SelectedSplats;
// etc.
```

**Step 3: Update Editor references**

Files that call Edit* methods on GaussianSplatRenderer:
- `Editor/GaussianSplatRendererEditor.cs` — calls `EditTranslateSelection`, `EditDeleteSelected`, etc.
- `Editor/GaussianToolContext.cs` — calls `EditUpdateSelection`, `EditStoreSelectionMouseDown`, etc.
- `Editor/GaussianMoveTool.cs` — calls `EditTranslateSelection`

Update these to go through `renderer.EditManager.XXX()` or keep forwarding methods on renderer for now.

Decision: **Keep forwarding methods** on `GaussianSplatRenderer` to minimize Editor changes in this PR. The forwarding methods simply delegate to `EditManager`.

**Step 4: Handle resource disposal**

In `GaussianSplatRenderer.OnDisable()`, add:
```csharp
_editManager?.Dispose();
_editManager = null;
```

Make `GaussianSplatEditManager` implement `IDisposable` to clean up GPU buffers.

**Step 5: Verify compilation**

Run: Open Unity, check Console.
Expected: Zero errors.

**Step 6: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianSplatEditManager.cs Assets/4DGS/Runtime/GaussianSplatEditManager.cs.meta Assets/4DGS/Runtime/GaussianSplatRenderer.cs
git commit -m "refactor: extract GaussianSplatEditManager from Renderer"
```

---

## Task 4: Clean up internal API boundaries

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`
- Modify: `Assets/4DGS/Runtime/GaussianAnimator.cs`
- Modify: `Assets/4DGS/Runtime/GaussianMorph.cs`

**Step 1: Replace direct field access in GaussianAnimator**

Current: `renderer._animOutputBuffer = buffer;` (direct internal field)

Add explicit method on `GaussianSplatRenderer`:
```csharp
internal void SetAnimationOutput(GraphicsBuffer animBuffer)
{
    _animOutputBuffer = animBuffer;
}

internal void ClearAnimationOutput()
{
    _animOutputBuffer = null;
}
```

Update `GaussianAnimator.cs` to use these methods instead of direct field access.

Also replace:
- `renderer._gpuChunks` -> `renderer.GpuChunksBuffer`
- `renderer._gpuChunksValid` -> `renderer.GpuChunksValid`

These internal properties already exist (lines 1016-1020), so just update GaussianAnimator to use them.

**Step 2: Replace direct field access in GaussianMorph**

Current: `renderer._morphedDataBuffer = buffer;` etc.

The `SetMorphData()` internal method already exists (line 1022). Verify `GaussianMorph` uses it. If it accesses fields directly anywhere, route through `SetMorphData()`.

**Step 3: Clean up remaining `internal` field exposure**

Change these from `internal` fields to `internal` properties with private backing fields:
- `_matSplats` -> keep internal (needed by RenderSystem)
- `_matComposite` -> keep internal (needed by RenderSystem)
- `_gpuSortKeys` -> make internal property
- `_gpuIndexBuffer` -> make internal property
- `_frameCounter` -> make internal property

**Step 4: Verify compilation**

Run: Open Unity, check Console.
Expected: Zero errors.

**Step 5: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianSplatRenderer.cs Assets/4DGS/Runtime/GaussianAnimator.cs Assets/4DGS/Runtime/GaussianMorph.cs
git commit -m "refactor: clean up internal API boundaries between renderer components"
```

---

## Task 5: Final verification

**Step 1: Full build check**

Open Unity project, verify:
- Zero compilation errors
- Zero new warnings in GaussianSplatting namespace
- Scene with Gaussian splats renders correctly (if test scene exists)

**Step 2: Verify file structure**

```
Assets/4DGS/Runtime/
  GaussianSplatRenderer.cs          (~600 lines - core component)
  GaussianSplatRenderSystem.cs      (~350 lines - rendering dispatch)
  GaussianTileRenderer.cs           (~300 lines - tile pipeline)
  GaussianSplatEditManager.cs       (~400 lines - edit operations)
  GaussianAnimator.cs               (unchanged logic, updated API calls)
  GaussianMorph.cs                  (unchanged logic, updated API calls)
  ... (other files unchanged)
```

**Step 3: Commit any final adjustments**

```bash
git add -A
git commit -m "refactor: finalize renderer split - verify clean build"
```

---

## Execution Order

Tasks MUST be done in order 1 -> 2 -> 3 -> 4 -> 5. Each task builds on the previous.

## Risk Notes

- **Compute shader bindings**: The `SetAssetDataOnCS()` method binds many buffers. After extracting EditManager, ensure the edit-related buffer bindings (selected/deleted bits) still work. The edit manager needs to update cutout buffers before view calc, so the call chain must be preserved.
- **`internal` access**: All new files are in the same assembly (`GaussianSplatting.asmdef`), so `internal` access works across files without issues.
- **Serialization**: No `[SerializeField]` changes. All serialized fields stay on `GaussianSplatRenderer`. Zero risk of breaking existing scenes/prefabs.
- **`GlobalOrderGroupCache`**: Stays in `GaussianSplatRenderSystem.cs` since both tile and non-tile paths use it. `GaussianTileRenderer` receives it as a parameter.
