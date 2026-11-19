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
- Optional per-renderer occlusion culling: splats lying behind a nearer surface of the same model are dropped using a coarse nearest-depth grid built from the splats themselves, so sparse point clouds stop showing what is behind them. Additive styles add light through what is behind them unless the style is marked Occludes, which keeps a glowing body from showing its inner layers (e.g. trousers under a jacket). A renderer can also be **Occluded By** another one: its splats behind that renderer's nearest surface are dropped, so particles and links born on the far side of a body stop showing through the near side
- Optional tile-based rendering path
- Render modes: full splats, point cloud, and debug views (points, indices, boxes, chunk bounds)
- URP Render Feature (primary target); HDRP pass and built-in pipeline paths exist but are less exercised

### Animation & Effects
- **GaussianPlayer** — 4D playback: plays per-frame splat asset sequences at a configurable frame rate
- **GaussianAnimator + GaussianAnimVolume** — procedural GPU animation, scoped by box/sphere volumes with falloff, non-destructive (runs on temporary buffers)
- **Modifier stack** — Dissolve, Wave, Warp, Property, Turbulence, Swirl, Caustic, WheatWave, Converge
- **GaussianModifierStateMachine** — state machine with parameter capture and blended transitions between modifier states
- **GaussianMorph** — GPU morph transition between two splat assets; the weight is a timeline channel
- **GaussianCutout** — box/ellipsoid cutout regions
- **GaussianStylizePostProcess** — stylization post-processing (stereo aware)
- **GaussianStyler + GaussianStyleVolume + GaussianSplatStyle** — per-splat stylized looks (color ramps by luminance, camera distance, position along a world axis or distance from the style volume, blended with the splat's own color, rim light and Fresnel edge glow, rim noise that breaks the edge into patches and lets silhouettes fade and spill points outward, dots, streaks, slices and grids, glints, thinning that can hold the point density on screen as the camera pulls away) blended in by weight. Like the animator and its volumes, a styler on the renderer takes the style volumes under it, each applying its own style in its region (Unbounded for the whole object, or e.g. the space around the camera); the strongest volume decides where they overlap, so one object can wear several styles. A volume's Scatter turns its falloff from a fade into a per-splat scatter: each splat in the falloff takes the style whole or not at all, more of them towards the inside, so a moving volume changes splats over one by one instead of along a line. Fading volume weights on the timeline switches and fades styles. A style is a list of blocks (Color, Rim, Occlusion, Depth, Shape, Quantize, Thinning, Glint, Flow, Blend), each one effect with its own settings: add the ones a look needs in the style's inspector. A new effect is a new block: a C# class under `Runtime/Style/Blocks`, its HLSL file under `Shaders/Style`, one line in `GaussianStyleBlocks` and its hook calls in `GaussianStyle.hlsl`
- **GaussianSplatLighting** — lights mesh-sampled splats with the scene's directional, point and spot lights and the ambient light, each splat by its own surface normal, before the style; the brightest shadow-casting light casts shadows from the splats themselves, so the source model can stay hidden. View-independent, so 360 capture faces and both eyes agree; particles are born lit. Turn off the mesh splat's baked lighting when using it
- **GaussianParticleEmitter + GaussianEmitVolume** — GPU particles shed from a splat renderer and drawn as splats of their own: each is born on the source as it is drawn (style included), can ride on the moving surface before it leaves (hovering above it and rising and falling with a wave that stays on the body), and is then carried by wind and curl-noise flow, with color, opacity and size following curves over its life. Birth volumes limit where particles come from (e.g. the hands); release volumes free attached particles as they sweep over the body. The simulation steps on a fixed time grid, so a timeline particle track gives the same particles on every take
- **GaussianParticleLinks** — lines between nearby particles of an emitter (the plexus look): each particle links to its nearest neighbours within a maximum distance, and a link fades out as its ends drift apart, breaking at that distance. The links are splats of their own renderer, drawn by a style with Segment streaks, so they take part in sorting, styles, ODS capture and depth of field like the particles themselves
- **GaussianGridLinks** — a lattice of points fixed in space that links to a target renderer as it passes: each lattice point near the target draws a line to its nearest target splats, fading as the target moves away and breaking at a maximum distance. From outside a surface the nearest splat lies along the normal, so the lines stand out of the body like pins; points a little farther out can show as faint dots. Meshes of the target's binding can be left out (e.g. a carried bag)
- **Look presets** (`GaussianLookPreset`) — save a renderer's look and bring it back later: its style volumes, particle emitters and particle links with a copy of the styles they use, plus the renderer's own look settings (scale, opacity, culling, styler, lighting). Save and Apply are in the renderer's inspector. Applying replaces the renderer's layers, writes the saved values back into the style assets the preset was saved from, and binds the timeline tracks of the replaced layers to the new ones by name; one undo step reverts it. The splat asset, the timeline and scene-wide objects (config, lights, post-processing) are not part of a preset
- **GaussianDepthOfField** — thin-lens depth of field on the camera, applied per splat: no depth buffer needed, works in Direct mode and 360 (ODS) capture
- **Tide timeline** (`Packages/com.hankun.tide`, a standalone package usable in other projects) — a deterministic timeline with no Unity Timeline dependency: clips with ease in/out and crossfades on channel, transform, activation, model animation (Animator clips, in edit mode too), audio, marker and nested sub-timeline tracks. Every frame depends on the time alone: rate-driven effects are integrated ahead into phase tables, and its scene clock times everything else, so seeking and scrubbing match playback and captures repeat take after take; the clock can follow an AudioSource. This project adds 4D sequence, modifier-state and particle tracks, and a capture track whose clips are the takes of the bound capture (Stereo360Capture for stereo 360, FlatCapture for the camera's ordinary view at a set resolution; both share SequenceCapture's takes, file names and background encoding): in play mode it jumps to each clip (after the capture's warmup frames), writes its frames numbered by their time in the clip, and can leave play mode when the last is done; and the renderer, volumes, styler, modifiers and depth of field expose channels. Edit it in *Window > Tide*, which previews on the scene without Play mode

![Unity4DGS in action](Docs/media/demo.gif)

### XR
- visionOS stereo rendering: single-dispatch stereo, foveated rendering (VRR) support
- Apple Vision Pro: supported only via the Metal rendering path (Compositor Services); the PolySpatial (RealityKit) path is not supported
- Quest is expected to work in principle, but is currently unverified

## Requirements

- Unity **6000.3+** with **URP 17.3**
- A GPU with compute shader support — best results on **Vulkan / Metal**
- Apple Vision Pro: the visionOS build support module and the Metal rendering mode

## Installation

Unity4DGS is a Unity package (`com.hankun.4dgs`); its manifest lives at `Assets/4DGS/package.json`. It depends on [Tide](https://github.com/HankunYu/Tide) (`com.hankun.tide`), a timeline package that is not on a package registry, so add Tide first.

**From Git URLs** — in the Package Manager, choose *Add package from git URL* twice, in this order:

```
https://github.com/HankunYu/Tide.git#v0.3.0
https://github.com/HankunYu/Unity4DGS.git?path=Assets/4DGS
```

The `#v0.3.0` tag pins the Tide version Unity4DGS is built against; `?path=` points at the package subfolder.

**From disk** — clone the repo with its submodules, then in the Package Manager choose *Add package from disk* and select `Packages/com.hankun.tide/package.json`, then `Assets/4DGS/package.json`:

```
git clone --recursive https://github.com/HankunYu/Unity4DGS.git
```

**Opening this repository as a project** — clone with `--recursive` as above (Tide is a submodule under `Packages/`). In an existing clone, run `git submodule update --init` first, or Unity reports missing `Tide` types.

## Getting Started

1. Add the `GaussianSplat URP Feature` to your URP Renderer asset.
2. Put a `GaussianSplatConfig` component in the scene (holds global rendering settings and shader references). Step 4 adds one if the scene has none.
3. Drop a `.ply` / `.spz` file into the project. It imports in place as a splat asset; quality, pruning, splat budget, SH degree and other options are in its import settings (Inspector), and the source file stays usable by other tools.
4. Drag the file into the Scene view or Hierarchy: this creates a GameObject with a `GaussianSplatRenderer` for it, flipped on Y because training tools write splats y-down. You can also add the component yourself and assign the asset.
5. For 4D sequences, put the per-frame files in a folder, right-click it and pick `Create > Gaussian Splatting > Sequence from Folder` (frames sort by natural file name order), then drag the sequence into the scene: that adds a `GaussianPlayer` that plays it.
6. To turn a model into splats without training, select the model (FBX or prefab) and pick `Create > Gaussian Splatting > Mesh Splats`. The new `.meshsplat` file imports as a splat asset sampled from every mesh under the model (skinned meshes in their bind pose), with splats lying flat on the surface. The import settings pick how splats are placed (Poisson disk for even coverage, random, one per vertex, denser along creases and open borders, LiDAR-style scan lines from virtual scanners, or a regular lattice projected onto the surface; any of them can drop the samples the model covers itself, such as clothing under clothing), where their color comes from (one color, the material's base color and texture, or vertex colors; individual materials can override it, e.g. a textured character among uniformly colored props), baked lighting and splat shape; and it re-imports when the model changes. Dragging it into the scene also brings in the model, with the splats below it and a `GaussianMeshDriver` that keeps them on the surface as the model animates (it skins on the GPU from the bone transforms, so the source meshes are hidden and need not render; blend shapes are not followed. Meshes listed under Vertex Animated in the import settings, such as the deforming meshes of an Alembic file, are read from their own vertices instead, as whatever plays the model left them). Animation modifiers then act on the moving surface. Set its Output to Sequence instead to bake a range of an animation clip into one splat asset per frame, played by a `GaussianPlayer`: either sampled once and carried along with the surface, or sampled anew every frame so scanners and sampling stay fixed while the model moves through them.

## Project Layout

```
Assets/4DGS/
├── Runtime/
│   ├── Rendering/    # Renderer, render system, config, URP feature, depth of field
│   ├── Sorting/      # GPU sorters
│   ├── Volumes/      # Shared volume base and shapes
│   ├── Animation/    # Animator, anim volumes, modifiers, state machine, morph
│   ├── Style/        # Styler, style volumes, style assets
│   ├── PostProcess/  # Stylize post-process volume component
│   ├── Playback/     # 4D sequence player
│   ├── Mesh/         # Mesh driver (splats on an animated model)
│   ├── Particles/    # Particle emitter and emit volumes
│   ├── Cutout/       # Cutout regions
│   ├── Capture/      # Stereo 360 capture
│   ├── Editing/      # Splat selection and editing state
│   └── Timeline/     # Tide tracks for 4D sequences, modifier states, particles and 360 capture
├── Editor/           # Inspectors in the same folders, plus Import (PLY/SPZ),
│                     # MeshSplat (mesh-to-splat importer) and editing tools
├── Shaders/          # Splat / point cloud shaders, sorting & morph compute
├── Resources/        # Compute shaders (animation, particles, tile rendering, utilities)
├── Presets/          # Style presets
└── Settings/         # URP pipeline assets

Packages/com.hankun.tide/  # Tide, the timeline (standalone package)
```

## License

MIT — see [LICENSE](LICENSE).

Portions copyright (c) 2023 Aras Pranckevičius ([UnityGaussianSplatting](https://github.com/aras-p/UnityGaussianSplatting)).
