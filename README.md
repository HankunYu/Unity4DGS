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
- Optional per-renderer backface culling for splats sampled from a mesh surface: a splat's local +Z is read as its outward surface normal, and splats facing away from the camera fade out near edge-on
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
- **GaussianSplatStyle + GaussianStyleVolume** — per-splat stylized looks (color ramps, rim light, dots, streaks, slices and grids, glints) blended in by weight; style volumes limit a style to a region, such as the space around the camera
- **GaussianDepthOfField** — thin-lens depth of field on the camera, applied per splat: no depth buffer needed, works in Direct mode and 360 (ODS) capture

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
2. Put a `GaussianSplatConfig` component in the scene (holds global rendering settings and shader references). Step 4 adds one if the scene has none.
3. Drop a `.ply` / `.spz` file into the project. It imports in place as a splat asset; quality, pruning, splat budget, SH degree and other options are in its import settings (Inspector), and the source file stays usable by other tools.
4. Drag the file into the Scene view or Hierarchy: this creates a GameObject with a `GaussianSplatRenderer` for it, flipped on Y because training tools write splats y-down. You can also add the component yourself and assign the asset.
5. For 4D sequences, put the per-frame files in a folder, right-click it and pick `Create > Gaussian Splats > Sequence from Folder` (frames sort by natural file name order), then drag the sequence into the scene: that adds a `GaussianPlayer` that plays it.
6. To turn a model into splats without training, select the model (FBX or prefab) and pick `Create > Gaussian Splats > Mesh Splats`. The new `.meshsplat` file imports as a splat asset sampled from every mesh under the model (skinned meshes in their bind pose), with splats lying flat on the surface. The import settings pick how splats are placed (Poisson disk for even coverage, random, one per vertex, denser along creases and open borders, LiDAR-style scan lines from virtual scanners, or a regular lattice projected onto the surface), where their color comes from (one color, the material's base color and texture, or vertex colors), baked lighting and splat shape; and it re-imports when the model changes. Dragging it into the scene also brings in the model, with the splats below it and a `GaussianMeshDriver` that keeps them on the surface as the model animates (it skins on the GPU from the bone transforms, so the source meshes are hidden and need not render; blend shapes are not followed). Animation modifiers then act on the moving surface. Set its Output to Sequence instead to bake a range of an animation clip into one splat asset per frame, played by a `GaussianPlayer`: either sampled once and carried along with the surface, or sampled anew every frame so scanners and sampling stay fixed while the model moves through them.

## Sample Assets

`DemoScene` references a Gaussian splat asset (a "ceramic" scene) generated with [World Labs](https://www.worldlabs.ai/) **Marble**. That asset is **not included in this repository** — a fresh clone will show a missing reference in `DemoScene`. Bring your own `.ply` / `.spz` and import it (see [Getting Started](#getting-started)) to populate the scene.

## Project Layout

```
Assets/4DGS/
├── Runtime/          # Renderer, sorting, animation system, modifiers, state machine
├── Editor/           # PLY/SPZ importer, mesh-to-splat importer, editing tools
├── Shaders/          # Splat / point cloud shaders, sorting & morph compute
├── Resources/        # Compute shaders (animation, tile rendering, utilities)
└── Settings/         # URP pipeline assets
```

## License

MIT — see [LICENSE](LICENSE).

Portions copyright (c) 2023 Aras Pranckevičius ([UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)).
