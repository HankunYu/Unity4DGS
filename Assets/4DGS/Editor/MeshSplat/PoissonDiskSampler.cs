using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Even coverage without clumps or gaps (blue noise): no two samples of a
    // mesh closer than spacing. Meshes are sampled independently, so samples
    // of touching meshes can come closer.
    [Serializable]
    public class PoissonDiskSampler : ISurfaceSampler
    {
        [Tooltip("Minimum distance between splats in metres. Halving it gives about four times the splats.")]
        [Min(0.0001f)] public float spacing = 0.003f;
        public int seed = 1;

        public List<SurfaceSample> Sample(IReadOnlyList<SurfaceMesh> meshes)
        {
            var result = new List<SurfaceSample>();
            for (int m = 0; m < meshes.Count; m++)
                PoissonDiskJob.Sample(meshes[m], m, spacing, null, seed, result);
            return result;
        }
    }
}
