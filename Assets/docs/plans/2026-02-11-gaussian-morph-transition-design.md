# Gaussian Splat Morph Transition Design

## Overview

A GPU-driven morph system that smoothly transitions between two Gaussian Splat assets by interpolating all per-splat properties (position, rotation, scale, opacity, color, SH coefficients). Operates as a pre-processing layer before the existing procedural animation system, enabling morph + modifier stacking.

## Requirements

- **Full property interpolation**: Position, rotation (slerp), scale, opacity, color, SH coefficients
- **Mixed splat count**: When two assets have different splat counts, shared splats interpolate by index, extra splats dissolve in/out
- **Non-destructive**: Original asset GPU buffers are never modified; output goes to an intermediate buffer
- **Modifier compatible**: Existing animation modifiers (Wave, Dissolve, Warp, Property) stack on top of morphed data
- **Format agnostic**: Source and target assets may use different compression formats
- **Animatable**: `weight` parameter (0~1) can be driven by Unity Animation/Timeline
- **Performance**: Real-time at 90fps for 500K+ splats on PC/VR

## Architecture

```
GaussianSplatRenderer
  ├── GaussianMorph (new component, same GameObject)
  │     ├── targetAsset: GaussianSplatAsset
  │     ├── weight: float [0,1]
  │     └── GaussianMorph.compute (dedicated compute shader)
  └── GaussianAnimator (optional, stacks on top)
        └── Volumes + Modifiers (unchanged)
```

## Data Flow

```
Without morph:
  AssetData → GPU Buffer → CalcViewData → Sort → Draw

With morph:
  Source Asset (compressed) ─┐
                             ├→ [MorphKernel] → _MorphedData (uncompressed)
  Target Asset (compressed) ─┘         │
                                       ↓
                             [AnimatePass] → _AnimOutput (offsets, unchanged)
                                       │
                                       ↓
                             [CalcViewData] ← reads _MorphedData + _AnimOutput
                                       │
                                       ↓
                                Sort → Draw
```

Key insight: Morph changes the "source data" that CalcViewData reads, while modifiers add offsets on top. These two layers are independent and composable.

## Execution Order

| Priority | Component | Action |
|----------|-----------|--------|
| 1 | `GaussianMorph` (`[DefaultExecutionOrder(-50)]`) | Upload target buffers, dispatch MorphKernel, set `_MorphedData` on renderer |
| 2 | `GaussianAnimator` (`[DefaultExecutionOrder(0)]`) | Collect volumes/modifiers, dispatch AnimateSplats, set `_AnimOutput` on renderer |
| 3 | `GaussianSplatRenderSystem` | CalcViewData reads `_MorphedData` + `_AnimOutput`, Sort, Draw |

## GPU Data Structures

### MorphedData Buffer Layout

`_MorphedData`: `RWStructuredBuffer<float4>`, 8 float4 per splat (128 bytes/splat).

| Slot | Content | Description |
|------|---------|-------------|
| `[base+0]` | `pos.xyz, opacity` | World-space position + opacity |
| `[base+1]` | `rot.xyzw` | Quaternion rotation |
| `[base+2]` | `scale.xyz, pad` | XYZ scale |
| `[base+3]` | `color.rgba` | Base color (linear RGB + alpha) |
| `[base+4]` | `sh[0..3]` | SH coefficients 0-3 |
| `[base+5]` | `sh[4..7]` | SH coefficients 4-7 |
| `[base+6]` | `sh[8..11]` | SH coefficients 8-11 |
| `[base+7]` | `sh[12..14], pad` | SH coefficients 12-14 |

Memory: 500K splats x 128B = ~61MB, 2M splats x 128B = ~244MB.

### Compute Shader Bindings

```hlsl
// Source asset buffers (from GaussianSplatRenderer, already bound)
ByteAddressBuffer   _SplatPos;
ByteAddressBuffer   _SplatOther;
ByteAddressBuffer   _SplatSH;
Texture2D<float4>   _SplatColor;
StructuredBuffer<SplatChunkInfo> _SplatChunks;

// Target asset buffers (uploaded by GaussianMorph)
ByteAddressBuffer   _TgtPos;
ByteAddressBuffer   _TgtOther;
ByteAddressBuffer   _TgtSH;
Texture2D<float4>   _TgtColor;
StructuredBuffer<SplatChunkInfo> _TgtChunks;

// Format parameters
uint  _SrcFormatFlags;   // packed: posFormat | (scaleFormat << 8) | (shFormat << 16)
uint  _TgtFormatFlags;
uint  _SrcSplatCount;
uint  _TgtSplatCount;
float _MorphWeight;

// Output
RWStructuredBuffer<float4> _MorphedData;
```

## MorphKernel Algorithm

```hlsl
[numthreads(64,1,1)]
void MorphSplats(uint3 id : SV_DispatchThreadID)
{
    uint idx = id.x;
    uint maxCount = max(_SrcSplatCount, _TgtSplatCount);
    if (idx >= maxCount) return;

    uint minCount = min(_SrcSplatCount, _TgtSplatCount);
    float w = _MorphWeight;

    float3 pos; float4 rot; float3 scale;
    float opacity; float4 color; float sh[15];

    if (idx < minCount)
    {
        // Shared splats: decode both, interpolate all properties
        SplatData src = DecodeSplatFrom(_SplatPos, _SplatOther, _SplatSH,
                                        _SplatColor, _SplatChunks,
                                        idx, _SrcFormatFlags);
        SplatData tgt = DecodeSplatFrom(_TgtPos, _TgtOther, _TgtSH,
                                        _TgtColor, _TgtChunks,
                                        idx, _TgtFormatFlags);

        pos     = lerp(src.pos, tgt.pos, w);
        rot     = slerp(src.rot, tgt.rot, w);  // quaternion spherical lerp
        scale   = lerp(src.scale, tgt.scale, w);
        opacity = lerp(src.opacity, tgt.opacity, w);
        color   = lerp(src.color, tgt.color, w);
        for (int i = 0; i < 15; i++)
            sh[i] = lerp(src.sh[i], tgt.sh[i], w);
    }
    else if (idx < _SrcSplatCount)
    {
        // Source-only splats: dissolve out
        SplatData src = DecodeSplatFrom(_SplatPos, _SplatOther, _SplatSH,
                                        _SplatColor, _SplatChunks,
                                        idx, _SrcFormatFlags);
        pos = src.pos; rot = src.rot; scale = src.scale;
        color = src.color;
        opacity = src.opacity * (1.0 - w);
        for (int i = 0; i < 15; i++) sh[i] = src.sh[i];
    }
    else // idx < _TgtSplatCount
    {
        // Target-only splats: dissolve in
        SplatData tgt = DecodeSplatFrom(_TgtPos, _TgtOther, _TgtSH,
                                        _TgtColor, _TgtChunks,
                                        idx, _TgtFormatFlags);
        pos = tgt.pos; rot = tgt.rot; scale = tgt.scale;
        color = tgt.color;
        opacity = tgt.opacity * w;
        for (int i = 0; i < 15; i++) sh[i] = tgt.sh[i];
    }

    // Write to _MorphedData
    uint base = idx * 8;
    _MorphedData[base + 0] = float4(pos, opacity);
    _MorphedData[base + 1] = rot;
    _MorphedData[base + 2] = float4(scale, 0);
    _MorphedData[base + 3] = color;
    _MorphedData[base + 4] = float4(sh[0], sh[1], sh[2], sh[3]);
    _MorphedData[base + 5] = float4(sh[4], sh[5], sh[6], sh[7]);
    _MorphedData[base + 6] = float4(sh[8], sh[9], sh[10], sh[11]);
    _MorphedData[base + 7] = float4(sh[12], sh[13], sh[14], 0);
}
```

### Quaternion Slerp

```hlsl
float4 slerp(float4 a, float4 b, float t)
{
    float d = dot(a, b);
    if (d < 0.0) { b = -b; d = -d; }   // take shortest path
    if (d > 0.9995)
        return normalize(lerp(a, b, t)); // near-parallel: nlerp fallback
    float theta = acos(d);
    float sinTheta = sin(theta);
    float wa = sin((1.0 - t) * theta) / sinTheta;
    float wb = sin(t * theta) / sinTheta;
    return wa * a + wb * b;
}
```

## CalcViewData Modification

In `SplatUtilities.compute`, the `CalcViewData` kernel gains a morph data branch:

```hlsl
StructuredBuffer<float4> _MorphedData;
uint _MorphDataValid;

// Inside CalcViewData kernel:
SplatData splat;
if (_MorphDataValid != 0)
{
    // Read pre-blended uncompressed data
    uint base = idx * 8;
    float4 d0 = _MorphedData[base + 0];
    float4 d1 = _MorphedData[base + 1];
    float4 d2 = _MorphedData[base + 2];
    float4 d3 = _MorphedData[base + 3];

    splat.pos     = d0.xyz;
    splat.opacity = (half)d0.w;
    splat.rot     = d1;
    splat.scale   = d2.xyz;
    splat.sh.col  = (half3)d3.rgb;

    // SH coefficients from slots 4-7
    float4 d4 = _MorphedData[base + 4];
    float4 d5 = _MorphedData[base + 5];
    float4 d6 = _MorphedData[base + 6];
    float4 d7 = _MorphedData[base + 7];
    // Assign to splat.sh.sh1..sh15
}
else
{
    // Original path: decode from compressed buffers
    splat = LoadSplatData(idx);
}

// Existing _AnimOutput application remains unchanged below
```

## Component Definitions

### GaussianMorph.cs

```csharp
[RequireComponent(typeof(GaussianSplatRenderer))]
[DefaultExecutionOrder(-50)]
public class GaussianMorph : MonoBehaviour
{
    public GaussianSplatAsset targetAsset;
    [Range(0f, 1f)] public float weight;

    // Internal state
    GaussianSplatRenderer m_Renderer;
    ComputeShader m_MorphCS;

    // Target asset GPU buffers
    GraphicsBuffer m_TgtPosData;
    GraphicsBuffer m_TgtOtherData;
    GraphicsBuffer m_TgtSHData;
    Texture2D      m_TgtColorData;
    GraphicsBuffer m_TgtChunks;

    // Output
    GraphicsBuffer m_MorphedData;  // StructuredBuffer<float4>, count = maxSplats * 8
    int m_MorphedSplatCount;       // max(src, tgt)

    void LateUpdate()
    {
        if (targetAsset == null || weight <= 0f)
        {
            // Disable morph: tell renderer to read original buffers
            m_Renderer.SetMorphData(null, 0, 0);
            return;
        }

        EnsureTargetBuffers();     // Upload target data if asset changed
        EnsureMorphedBuffer();     // Resize _MorphedData if needed

        // Bind buffers + dispatch MorphKernel
        DispatchMorph();

        // Pass morph result to renderer
        m_Renderer.SetMorphData(m_MorphedData, m_MorphedSplatCount, 1);
    }
}
```

### GaussianSplatRenderer Additions

```csharp
// New fields
internal GraphicsBuffer m_MorphedDataBuffer;
internal int m_MorphDataValid;
internal int m_MorphedSplatCount;

// Called by GaussianMorph
public void SetMorphData(GraphicsBuffer morphedData, int splatCount, int valid)
{
    m_MorphedDataBuffer = morphedData;
    m_MorphedSplatCount = splatCount;
    m_MorphDataValid = valid;
}

// In SetAssetDataOnCS() or equivalent, bind to compute shaders:
cmd.SetComputeBufferParam(cs, kernel, "_MorphedData", m_MorphedDataBuffer);
cmd.SetComputeIntParam(cs, "_MorphDataValid", m_MorphDataValid);
// When morph is active, use m_MorphedSplatCount for dispatch size
```

## Shared Decode Functions

Extract from existing `GaussianAnimate.compute` into `GaussianSplatDecode.hlsl`:

```hlsl
// GaussianSplatDecode.hlsl
// Parameterized decode functions that accept buffer + format arguments

float3 DecodePosFrom(ByteAddressBuffer buf, StructuredBuffer<SplatChunkInfo> chunks,
                     uint idx, uint posFormat);
float4 DecodeRotFrom(ByteAddressBuffer buf, uint idx);
float3 DecodeScaleFrom(ByteAddressBuffer buf, uint idx, uint scaleFormat);
float4 DecodeColorFrom(Texture2D<float4> tex, uint idx);
void   DecodeSHFrom(ByteAddressBuffer buf, uint idx, uint shFormat, out float sh[15]);
```

Both `GaussianAnimate.compute` and `GaussianMorph.compute` include this file.

## File Structure

```
4DGS/
├── Runtime/
│   ├── GaussianMorph.cs               // Morph controller component
│   ├── GaussianSplatRenderer.cs        // +SetMorphData(), +morph buffer bindings
│   └── ...
├── Shaders/
│   ├── GaussianMorph.compute           // MorphSplats kernel
│   ├── GaussianSplatDecode.hlsl        // Shared decode functions (extracted)
│   ├── GaussianAnimate.compute         // Refactored to use GaussianSplatDecode.hlsl
│   ├── SplatUtilities.compute          // +_MorphedData branch in CalcViewData
│   └── ...
├── Editor/
│   └── GaussianMorphEditor.cs          // Custom inspector (weight slider, preview)
```

## Resource Management

### Target Buffer Lifecycle

- Target GPU buffers are created when `targetAsset` is assigned and uploaded once
- When `targetAsset` changes, old buffers are released and new ones created
- When `weight == 0` and no animation is driving it, buffers can be lazily released

### MorphedData Buffer Sizing

- Allocated as `max(srcSplatCount, tgtSplatCount) * 8` float4 elements
- Reallocated only when splat counts change (asset swap)
- Released when GaussianMorph component is disabled/destroyed

### Morph Completion

- When `weight` reaches 1.0, optionally:
  - Swap renderer's source asset to targetAsset
  - Disable GaussianMorph to free GPU memory
  - Fire `OnMorphComplete` event for game logic

### Coexistence with GaussianPlayer

- Player switches source asset each frame; GaussianMorph target stays fixed
- Enables "4D sequence playback morphing into a static target" effect
- MorphKernel re-blends each frame based on current source data

## Performance Considerations

- Single compute dispatch per frame (64 threads/group)
- 500K splats: ~61MB for _MorphedData, dispatch ~7800 groups
- Two full asset decode per splat in MorphKernel (main cost), but only when morph is active
- When `weight == 0`, no dispatch occurs, zero overhead
- Shared decode functions avoid code duplication without runtime cost

## Implementation Priority

1. `GaussianSplatDecode.hlsl` — extract shared decode functions from existing shaders
2. `GaussianMorph.compute` — MorphKernel with full property interpolation
3. `GaussianMorph.cs` — component with target buffer management + dispatch
4. `SplatUtilities.compute` — CalcViewData `_MorphedData` branch
5. `GaussianSplatRenderer.cs` — `SetMorphData()` + buffer binding plumbing
6. `GaussianMorphEditor.cs` — custom inspector with weight slider
7. Testing with same-format and cross-format asset pairs
