# Extract Global Config — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Extract global rendering configuration (render mode, shaders, compute shaders) from per-instance `GaussianSplatRenderer` into a dedicated `GaussianSplatConfig` scene component, with all shader/compute resources auto-loaded.

**Architecture:** New `GaussianSplatConfig` MonoBehaviour holds global settings (`renderMode`, `pointDisplaySize`, `useTileRenderer`) and auto-loads all shader/compute resources via `Shader.Find()` / `Resources.Load()`. `GaussianSplatRenderSystem` caches a reference to Config and reads settings from it. Renderer keeps read-only forwarding properties (`csSplatUtilities`) to minimize churn in EditManager/TileRenderer/Morph.

**Tech Stack:** Unity 2022.3+, C#, ComputeShader, URP/HDRP conditional compilation

---

### Task 1: Move SplatUtilities.compute to Resources

Move the compute shader so it can be auto-loaded via `Resources.Load`.

**Files:**
- Move: `Assets/4DGS/Shaders/SplatUtilities.compute` → `Assets/4DGS/Resources/SplatUtilities.compute`
- Move: `Assets/4DGS/Shaders/SplatUtilities.compute.meta` → `Assets/4DGS/Resources/SplatUtilities.compute.meta`

**Step 1: Move the file and its meta**

```bash
git mv Assets/4DGS/Shaders/SplatUtilities.compute Assets/4DGS/Resources/SplatUtilities.compute
```

**Step 2: Commit**

```bash
git add -A
git commit -m "refactor: move SplatUtilities.compute to Resources for auto-loading"
```

---

### Task 2: Create GaussianSplatRenderMode enum

Extract the `RenderMode` enum to namespace level.

**Files:**
- Create: `Assets/4DGS/Runtime/GaussianSplatRenderMode.cs`

**Step 1: Create the enum file**

```csharp
namespace GaussianSplatting.Runtime
{
    public enum GaussianSplatRenderMode
    {
        Splats,
        DebugPoints,
        DebugPointIndices,
        DebugBoxes,
        DebugChunkBounds,
    }
}
```

**Step 2: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianSplatRenderMode.cs
git commit -m "refactor: extract GaussianSplatRenderMode to namespace level"
```

---

### Task 3: Create GaussianSplatConfig

**Files:**
- Create: `Assets/4DGS/Runtime/GaussianSplatConfig.cs`

**Step 1: Create the config component**

```csharp
using UnityEngine;

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

**Step 2: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianSplatConfig.cs
git commit -m "feat: add GaussianSplatConfig for global render settings"
```

---

### Task 4: Update GaussianSplatRenderSystem to use Config

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs`

**Changes:**
1. Add `_config` field and `Config` property with `FindFirstObjectByType` lookup
2. Add `_configWarningLogged` to throttle warnings
3. In `GatherSplatsForCamera`: check config validity first, return false if missing
4. In `CanUseGlobalSortPath`: check `_config.renderMode` instead of iterating renderers
5. In `EnsureGlobalSorter`: use `_config.CsSplatUtilities` instead of `reference.csSplatUtilities`
6. In `SortAndRenderSplatsGlobal`: use `_config.useTileRenderer` and `_config.CsTileRender`
7. In `SortAndRenderSplatsPerObject`: use `_config.renderMode`, `_config.pointDisplaySize`
8. Add `Materials` cache (splats/composite/debug) on RenderSystem, created from Config shaders
9. Expose `Config` as public read-only property

**Key code patterns:**

```csharp
// Config lookup
private GaussianSplatConfig _config;
private bool _configWarningLogged;

public GaussianSplatConfig Config
{
    get
    {
        if (_config == null)
        {
            _config = Object.FindFirstObjectByType<GaussianSplatConfig>();
            if (_config == null && !_configWarningLogged)
            {
                Debug.LogWarning("GaussianSplatConfig not found in scene. Gaussian splat rendering disabled.");
                _configWarningLogged = true;
            }
            else if (_config != null)
            {
                _configWarningLogged = false;
            }
        }
        return _config;
    }
}

// In GatherSplatsForCamera, at start:
var config = Config;
if (config == null || !config.ResourcesValid)
    return false;
```

```csharp
// CanUseGlobalSortPath — simplified
private bool CanUseGlobalSortPath()
{
    if (_config.renderMode != GaussianSplatRenderMode.Splats)
        return false;
    foreach (var kvp in _activeSplats)
    {
        var gs = kvp.Item1;
        if (!gs.SupportsGlobalSortPath())
            return false;
    }
    return true;
}
```

```csharp
// EnsureGlobalSorter — use config
private bool EnsureGlobalSorter()
{
    var cs = _config.CsSplatUtilities;
    if (cs == null) return false;
    if (_globalSorter == null || _globalSorterShader != cs)
    {
        _globalSorter = new GpuSorting(cs);
        _globalSorterShader = cs;
    }
    return _globalSorter != null && _globalSorter.Valid;
}
```

```csharp
// Materials cache on RenderSystem
private Material _matSplats;
private Material _matComposite;
private Material _matDebugPoints;
private Material _matDebugBoxes;
private GaussianSplatConfig _materialSource; // track which config created them

internal void EnsureMaterials()
{
    if (_config == null) return;
    if (_matSplats == null || _materialSource != _config)
    {
        Object.DestroyImmediate(_matSplats);
        Object.DestroyImmediate(_matComposite);
        Object.DestroyImmediate(_matDebugPoints);
        Object.DestroyImmediate(_matDebugBoxes);
        _matSplats = new Material(_config.ShaderSplats) { name = "GaussianSplats" };
        _matComposite = new Material(_config.ShaderComposite) { name = "GaussianClearDstAlpha" };
        _matDebugPoints = new Material(_config.ShaderDebugPoints) { name = "GaussianDebugPoints" };
        _matDebugBoxes = new Material(_config.ShaderDebugBoxes) { name = "GaussianDebugBoxes" };
        _materialSource = _config;
    }
}
```

```csharp
// SortAndRenderSplatsPerObject — read from config
Material displayMat = _config.renderMode switch
{
    GaussianSplatRenderMode.DebugPoints => _matDebugPoints,
    GaussianSplatRenderMode.DebugPointIndices => _matDebugPoints,
    GaussianSplatRenderMode.DebugBoxes => _matDebugBoxes,
    GaussianSplatRenderMode.DebugChunkBounds => _matDebugBoxes,
    _ => _matSplats
};
// ...
mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, _config.pointDisplaySize);
mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex,
    _config.renderMode == GaussianSplatRenderMode.DebugPointIndices ? 1 : 0);
mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks,
    _config.renderMode == GaussianSplatRenderMode.DebugChunkBounds ? 1 : 0);
// ...
if (_config.renderMode is GaussianSplatRenderMode.DebugBoxes or GaussianSplatRenderMode.DebugChunkBounds)
    indexCount = 36;
if (_config.renderMode == GaussianSplatRenderMode.DebugChunkBounds)
    instanceCount = gs.GpuChunksValid ? gs.GpuChunksBuffer.count : 0;
```

**Step 3: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs
git commit -m "refactor: read global settings from GaussianSplatConfig in RenderSystem"
```

---

### Task 5: Slim down GaussianSplatRenderer

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

**Changes:**
1. Remove inner `RenderMode` enum
2. Remove fields: `renderMode`, `pointDisplaySize`, `shaderSplats`, `shaderComposite`, `shaderDebugPoints`, `shaderDebugBoxes`, `csSplatUtilities`, `csTileRender`, `useTileRenderer` and all their `[FormerlySerializedAs]`
3. Remove Material fields (`_matSplats`, `_matComposite`, `_matDebugPoints`, `_matDebugBoxes`) and related properties (`MatSplats`, `MatComposite`, `MatDebugPoints`, `MatDebugBoxes`)
4. Remove `EnsureMaterials()` method
5. Add forwarding property for compute shader: `internal ComputeShader csSplatUtilities => GaussianSplatRenderSystem.instance.Config?.CsSplatUtilities;`
6. Update `resourcesAreSetUp` to check `GaussianSplatRenderSystem.instance.Config?.ResourcesValid`
7. Update `OnEnable` / `OnDisable` — remove material creation/destruction
8. Remove `EnsureSorterAndRegister` dependency on `resourcesAreSetUp` — rely on Config

**Key forwarding properties to add:**

```csharp
// Forwarding properties for compute shader access (minimizes EditManager/Morph churn)
internal ComputeShader csSplatUtilities => GaussianSplatRenderSystem.instance.Config?.CsSplatUtilities;
```

**Step 4: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianSplatRenderer.cs
git commit -m "refactor: remove global config fields from GaussianSplatRenderer"
```

---

### Task 6: Update GaussianTileRenderer

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianTileRenderer.cs`

**Changes:**
1. `EnsureResources` signature: remove `GaussianSplatRenderer reference`, take `GaussianSplatConfig config` instead (or read from RenderSystem)
2. Load `_tileCs` from `config.CsTileRender` instead of `reference.csTileRender`
3. Create `_tileSorter` from `config.CsSplatUtilities` instead of `reference.csSplatUtilities`

**Step 2: Commit**

```bash
git add Assets/4DGS/Runtime/GaussianTileRenderer.cs
git commit -m "refactor: use GaussianSplatConfig in TileRenderer"
```

---

### Task 7: Update Editor — GaussianSplatRendererEditor

**Files:**
- Modify: `Assets/4DGS/Editor/GaussianSplatRendererEditor.cs`

**Changes:**
1. Remove SerializedProperties: `m_PropRenderMode`, `m_PropPointDisplaySize`, `m_PropShaderSplats`, `m_PropShaderComposite`, `m_PropShaderDebugPoints`, `m_PropShaderDebugBoxes`, `m_PropCSSplatUtilities`, `m_PropCSTileRender`, `m_PropUseTileRenderer`
2. Remove their `FindProperty` calls in `OnEnable`
3. Remove "Debugging Tweaks" section (render mode, point display size)
4. Remove "Resources" foldout section
5. Remove `useTileRenderer` line from "Render Options"
6. Update tile pair stats display — read `useTileRenderer` from Config instead of renderer

**Step 2: Commit**

```bash
git add Assets/4DGS/Editor/GaussianSplatRendererEditor.cs
git commit -m "refactor: remove global config fields from RendererEditor"
```

---

### Task 8: Create GaussianSplatConfigEditor

**Files:**
- Create: `Assets/4DGS/Editor/GaussianSplatConfigEditor.cs`

**Step 1: Create the editor**

```csharp
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatConfig))]
    public class GaussianSplatConfigEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var config = (GaussianSplatConfig)target;

            DrawDefaultInspector();

            EditorGUILayout.Space();
            GUILayout.Label("Auto-Loaded Resources", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Splat Shader", config.ShaderSplats, typeof(Shader), false);
                EditorGUILayout.ObjectField("Composite Shader", config.ShaderComposite, typeof(Shader), false);
                EditorGUILayout.ObjectField("Debug Points Shader", config.ShaderDebugPoints, typeof(Shader), false);
                EditorGUILayout.ObjectField("Debug Boxes Shader", config.ShaderDebugBoxes, typeof(Shader), false);
                EditorGUILayout.ObjectField("SplatUtilities CS", config.CsSplatUtilities, typeof(ComputeShader), false);
                EditorGUILayout.ObjectField("TileRender CS", config.CsTileRender, typeof(ComputeShader), false);
            }

            if (!config.ResourcesValid)
                EditorGUILayout.HelpBox("Some shader resources failed to load. Check console for details.", MessageType.Error);

            serializedObject.ApplyModifiedProperties();
        }
    }
}
```

**Step 2: Commit**

```bash
git add Assets/4DGS/Editor/GaussianSplatConfigEditor.cs
git commit -m "feat: add GaussianSplatConfigEditor with auto-loaded resource display"
```

---

### Task 9: Verify compilation and commit all meta files

**Step 1: Check for missing meta files**

```bash
find Assets/4DGS -name "*.cs" ! -name "*.meta" | while read f; do [ ! -f "$f.meta" ] && echo "Missing: $f.meta"; done
```

**Step 2: Run headless compile check**

```bash
$UNITY -projectPath "$(pwd)" -batchmode -quit -logFile Logs/import.log
```

Review `Logs/import.log` for compilation errors.

**Step 3: Final commit with any remaining meta files**

```bash
git add -A
git commit -m "chore: add meta files for new scripts"
```

---

## Summary of changes

| File | Action |
|------|--------|
| `Shaders/SplatUtilities.compute` | **Move** → `Resources/` |
| `Runtime/GaussianSplatRenderMode.cs` | **New** — namespace-level enum |
| `Runtime/GaussianSplatConfig.cs` | **New** — global config MonoBehaviour, auto-loads shaders |
| `Runtime/GaussianSplatRenderer.cs` | **Modify** — remove global fields, add forwarding property |
| `Runtime/GaussianSplatRenderSystem.cs` | **Modify** — cache Config, move materials here, read global settings |
| `Runtime/GaussianTileRenderer.cs` | **Modify** — use Config for compute shader refs |
| `Editor/GaussianSplatRendererEditor.cs` | **Modify** — remove global field UI |
| `Editor/GaussianSplatConfigEditor.cs` | **New** — Inspector for Config |
