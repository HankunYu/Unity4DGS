using System;
using System.Collections.Generic;
using GaussianSplatting.Editor.Import;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Everything that turns a model into splats. Each sampler and color
    // source keeps its own settings, so switching between them loses nothing.
    [Serializable]
    public class MeshSplatSettings
    {
        public MeshSplatOutput output = MeshSplatOutput.Asset;
        public MeshSplatSequenceSettings sequence = new();

        public SurfaceSamplerKind sampler = SurfaceSamplerKind.PoissonDisk;
        public PoissonDiskSampler poissonDisk = new();
        public RandomSampler random = new();
        public VertexSampler vertex = new();
        public FeatureSampler feature = new();
        public ScanlineSampler scanline = new();
        public GridSampler grid = new();

        // Source for every surface whose material has no override
        public SplatColorKind color = SplatColorKind.Uniform;
        public List<MaterialColorOverride> colorOverrides = new();
        public UniformColorSource uniformColor = new();
        public MaterialColorSource materialColor = new();
        public VertexColorSource vertexColor = new();

        // Multiplies the color by a fixed directional light. It is baked
        // into the splats, so it only holds while the model keeps its pose.
        public bool bakeLighting = true;
        // Direction towards the light, in the model's space
        public Vector3 lightDirection = new(0.4f, 0.7f, -0.6f);
        [Range(0f, 1f)] public float ambient = 0.35f;

        // Splat sigma relative to the sampler's local spacing (the typical
        // distance to neighbouring samples), which makes neighbouring splats
        // overlap enough to read as a surface
        [Min(0.01f)] public float splatSize = 0.5f;
        // Sigma along the surface normal relative to splatSize: thin discs
        // lying on the surface
        [Range(0.01f, 1f)] public float splatThickness = 0.1f;
        [Range(0.01f, 1f)] public float opacity = 0.95f;

        public GaussianSplatQuality quality = GaussianSplatQuality.Medium;
        // Used only when quality is Custom
        public GaussianSplatFormats customFormats = GaussianSplatFormats.ForQuality(GaussianSplatQuality.Medium);

        public ISurfaceSampler Sampler => sampler switch
        {
            SurfaceSamplerKind.PoissonDisk => poissonDisk,
            SurfaceSamplerKind.Random => random,
            SurfaceSamplerKind.Vertex => vertex,
            SurfaceSamplerKind.Feature => feature,
            SurfaceSamplerKind.Scanline => scanline,
            SurfaceSamplerKind.Grid => grid,
            _ => throw new ArgumentOutOfRangeException(nameof(sampler), sampler, null),
        };

        public ISplatColorSource ColorSource(SplatColorKind kind) => kind switch
        {
            SplatColorKind.Uniform => uniformColor,
            SplatColorKind.Material => materialColor,
            SplatColorKind.VertexColor => vertexColor,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        // The first override for the material wins; surfaces without a
        // material use the default source
        public SplatColorKind ColorKindFor(Material material)
        {
            if (material != null)
            {
                foreach (MaterialColorOverride entry in colorOverrides)
                {
                    if (entry != null && entry.material == material)
                        return entry.color;
                }
            }
            return color;
        }

        // Generated splats are all needed (they are bound to the surface) and
        // have no SH, so only the data formats carry over
        public GaussianSplatImportSettings BuildSettings() => new()
        {
            quality = quality,
            customFormats = customFormats,
            importCameras = false,
            maxSHDegree = 0,
        };
    }
}
