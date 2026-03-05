# Procedural Splat Animation System Design

## Overview

A GPU-driven, volume-based procedural animation system for Gaussian Splats. Enables per-splat transformations (position, scale, opacity, color) within user-defined spatial regions, targeting the Blue Lethe immersive film project.

## Requirements

- **Region selection**: Box/Sphere volumes to define which splats are affected
- **Effects**: Dissolve (scatter/gather), Wave (oscillation/breathing), Warp (spatial distortion), Property animation (opacity/scale/color)
- **Isolation**: Only splats inside a volume are affected; others remain untouched
- **Non-destructive**: Animations use a temporary buffer; original asset data is never modified
- **Performance**: Real-time at 90fps for 500K-2M splats on PC/VR

## Architecture

```
GaussianSplatRenderer
  └── GaussianAnimator (new component, same GameObject)
        ├── GaussianAnimVolume[] (scene objects defining regions)
        │     ├── Shape: Box / Sphere
        │     ├── Falloff: edge gradient distance
        │     └── GaussianAnimModifier[] (effect stack)
        │           ├── DissolveModifier
        │           ├── WaveModifier
        │           ├── WarpModifier
        │           └── PropertyModifier
        └── GaussianAnimate.compute (GPU animation kernel)
```

## Data Flow

```
Original flow:
  AssetData → GPU Buffer → CalcViewData → Sort → Draw

New flow:
  AssetData → GPU Buffer → [AnimatePass] → CalcViewData → Sort → Draw
                               ↑
                        GaussianAnimator injects animation
```

The AnimatePass:
1. GaussianAnimator collects all Volume transforms and Modifier parameters
2. Packs them into a structured array uploaded to GPU
3. ComputeShader iterates each splat:
   - For each Volume, test if splat is inside + compute edge weight (0~1)
   - Accumulate all Modifier transforms: position offset, scale factor, opacity factor, color tint
   - Write transformed data to a **temporary buffer** (original data untouched)
4. CalcViewData reads from the temporary buffer when animation is active

## GPU Data Structures

```hlsl
struct AnimVolumeData
{
    float4x4 worldToLocal;     // volume world-to-local matrix
    float4   shapeParams;      // x: type (0=box, 1=sphere), y: falloff distance, zw: reserved
    float4   boundsSize;       // box half-extents or sphere radius
};

struct AnimModifierData
{
    int      volumeIndex;      // which volume this modifier belongs to
    int      modifierType;     // 0=dissolve, 1=wave, 2=warp, 3=property
    float    time;             // current animation time
    float    pad0;

    // modifier-specific params (16 floats, interpreted per type)
    float4   params0;
    float4   params1;
    float4   params2;
    float4   params3;
};
```

## Component Definitions

### GaussianAnimator.cs

```
[RequireComponent(typeof(GaussianSplatRenderer))]
public class GaussianAnimator : MonoBehaviour
{
    public GaussianAnimVolume[] volumes;

    // Internal: manages temp GPU buffers, dispatches compute shader
    // Hooks into GaussianSplatRenderer before CalcViewData
}
```

### GaussianAnimVolume.cs

```
public class GaussianAnimVolume : MonoBehaviour
{
    public enum VolumeShape { Box, Sphere }

    public VolumeShape shape = VolumeShape.Box;
    [Range(0f, 5f)] public float falloff = 0.5f;

    // Modifiers are sibling components on the same GameObject
    // Collected automatically via GetComponents<GaussianAnimModifier>()
}
```

### GaussianAnimModifier.cs (base class)

```
public abstract class GaussianAnimModifier : MonoBehaviour
{
    public abstract int ModifierType { get; }
    public abstract void FillParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3);
}
```

### DissolveModifier

Scatters splats outward from their original position or gathers them back.

| Parameter | Type | Range | Description |
|-----------|------|-------|-------------|
| direction | Vector3 | normalized | Scatter direction |
| strength | float | 0~1 | 0 = fully gathered, 1 = fully scattered |
| noiseScale | float | 0.1~10 | Per-splat randomization frequency |
| noiseSpeed | float | 0~5 | Noise evolution over time |

Algorithm: Per-splat pseudo-random offset based on position hash, multiplied by strength. Opacity fades to 0 as strength approaches 1.

### WaveModifier

Periodic oscillation applied to splat positions.

| Parameter | Type | Range | Description |
|-----------|------|-------|-------------|
| waveAxis | Vector3 | normalized | Wave propagation direction |
| amplitude | float | 0~5 | Wave height |
| frequency | float | 0.1~10 | Wave spatial frequency |
| speed | float | 0~10 | Wave temporal speed |

Algorithm: Displacement = amplitude * sin(dot(pos, waveAxis) * frequency + time * speed) in the direction perpendicular to waveAxis.

### WarpModifier

Non-linear spatial distortion.

| Parameter | Type | Range | Description |
|-----------|------|-------|-------------|
| warpType | enum | Twist/Bend/Spherize/Pinch | Type of distortion |
| axis | Vector3 | normalized | Distortion axis |
| strength | float | -5~5 | Distortion intensity |
| center | Vector3 | local coords | Center point relative to volume |

Algorithm: Transform splat position into volume local space, apply non-linear function based on warpType, transform back to world space.

### PropertyModifier

Animate splat visual properties without moving them.

| Parameter | Type | Range | Description |
|-----------|------|-------|-------------|
| opacityMultiplier | float | 0~2 | Opacity scale factor |
| scaleMultiplier | float | 0~3 | Size scale factor |
| colorTint | Color | RGBA | Color overlay |
| colorBlend | float | 0~1 | Blend strength for color tint |

Algorithm: Direct multiplication/lerp on splat opacity, scale, and color values.

## File Structure

```
4DGS/
├── Runtime/
│   ├── GaussianAnimator.cs          // Main controller
│   ├── GaussianAnimVolume.cs        // Volume definition
│   └── Modifiers/
│       ├── GaussianAnimModifier.cs  // Base class
│       ├── DissolveModifier.cs      // Scatter/gather effect
│       ├── WaveModifier.cs          // Wave/breathing effect
│       ├── WarpModifier.cs          // Spatial distortion
│       └── PropertyModifier.cs      // Opacity/scale/color animation
├── Shaders/
│   └── GaussianAnimate.compute      // GPU animation kernel
```

## Integration Points

1. **GaussianSplatRenderer.Update()**: GaussianAnimator hooks in before CalcViewData to swap buffer references
2. **SplatUtilities.compute**: May need a new kernel or the animation compute runs separately
3. **GaussianCutout**: Animation volumes share a similar spatial testing pattern

## Implementation Priority

1. GaussianAnimVolume + basic Box/Sphere shape testing
2. GaussianAnimator + temp buffer management + compute dispatch
3. PropertyModifier (simplest, validates the pipeline)
4. DissolveModifier (validates position modification)
5. WaveModifier (validates time-based animation)
6. WarpModifier (validates complex spatial transforms)

## Performance Considerations

- Single compute dispatch per frame for all volumes + modifiers
- Volume/modifier data packed into a single structured buffer (max ~16 volumes, ~32 modifiers)
- Early-out for splats with zero total weight across all volumes
- Temporary buffer only allocated when animator is active
- ~40 bytes per splat for temp position data, ~80MB for 2M splats
