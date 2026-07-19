# Unity4DGS — Gaussian Splatting for Unity

**English** · [中文](README.zh-CN.md)

Real-time 3D/4D Gaussian Splatting renderer and procedural animation toolkit for Unity 6+ / URP, with visionOS / VR stereo support.

[![Unity4DGS demo video](https://img.youtube.com/vi/X9RL0WqCAvM/maxresdefault.jpg)](https://youtu.be/X9RL0WqCAvM)

Based on [aras-p/UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting) (MIT), heavily extended with a GPU-driven animation system, 4D sequence playback, morphing, and XR rendering paths.

## Features

### Rendering
- GPU-sorted splat rendering with radix sort (counting-sort fallback on platforms without wave intrinsics)
- Global cross-model sorting: overlapping splat models blend and occlude correctly
- GPU frustum + opacity culling with indirect draw
- Optional tile-based rendering path
- Render modes: full splats, point cloud, and debug views (points, indices, boxes, chunk bounds)
- URP Render Feature (primary target); HDRP pass and built-in pipeline paths exist but are less exercised

### Animation & Effects
- **GaussianPlayer** — 4D playback: plays per-frame splat asset sequences at a configurable frame rate
- **GaussianAnimator + GaussianAnimVolume** — procedural GPU animation, scoped by box/sphere volumes with falloff, non-destructive (runs on temporary buffers)
- **Modifier stack** — Dissolve, Wave, Warp, Property, Turbulence, Swirl, Caustic, WheatWave, Converge
- **GaussianModifierStateMachine** — state machine with parameter capture and blended transitions between modifier states
- **GaussianMorph** — GPU morph transition between two splat assets
- **GaussianCutout** — box/ellipsoid cutout regions
- **GaussianStylizeVolume** — stylization post-processing (stereo aware)

![Unity4DGS in action](Docs/media/demo.gif)

### XR
- visionOS stereo rendering: single-dispatch stereo, foveated rendering (VRR) support
- Apple Vision Pro: supported only via the Metal rendering path (Compositor Services); the PolySpatial (RealityKit) path is not supported
- Quest is expected to work in principle, but is currently unverified

## Requirements

- Unity **2022.3+** with **URP**
- A GPU with compute shader support — best results on **Vulkan / Metal**

## Installation

Unity4DGS is a Unity package (`com.hankun.4dgs`); its manifest lives at `Assets/4DGS/package.json`. Add it to an existing project either way:

**From a Git URL** — in the Package Manager, choose *Add package from git URL* and enter (the `?path=` points at the package subfolder):

```
https://github.com/HankunYu/Unity4DGS.git?path=Assets/4DGS
```

**From disk** — clone the repo, then in the Package Manager choose *Add package from disk* and select `Assets/4DGS/package.json`:

```
git clone https://github.com/HankunYu/Unity4DGS.git
```

## Getting Started

1. Add the `GaussianSplat URP Feature` to your URP Renderer asset.
2. Put a `GaussianSplatConfig` component in the scene (holds global rendering settings and shader references).
3. Create a splat asset from a `.ply` / `.spz` file via `Tools > Gaussian Splats > Create GaussianSplatAsset`.
4. Add a `GaussianSplatRenderer` component to a GameObject and assign the asset.
5. For 4D sequences, use `GaussianPlayer` and point it at a folder of per-frame assets.

## Sample Assets

`DemoScene` references a Gaussian splat asset (a "ceramic" scene) generated with [World Labs](https://www.worldlabs.ai/) **Marble**. That asset is **not included in this repository** — a fresh clone will show a missing reference in `DemoScene`. Bring your own `.ply` / `.spz` and create an asset (see [Getting Started](#getting-started)) to populate the scene.

## Project Layout

```
Assets/4DGS/
├── Runtime/          # Renderer, sorting, animation system, modifiers, state machine
├── Editor/           # Asset creator, importers (PLY/SPZ), editing tools
├── Shaders/          # Splat / point cloud shaders, sorting & morph compute
├── Resources/        # Compute shaders (animation, tile rendering, utilities)
└── Settings/         # URP pipeline assets
```

## License

MIT — see [LICENSE](LICENSE).

Portions copyright (c) 2023 Aras Pranckevičius ([UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)).
