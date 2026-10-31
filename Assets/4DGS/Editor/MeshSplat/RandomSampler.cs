using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Random = Unity.Mathematics.Random;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Independent uniform points over the surface (white noise): the same
    // density as Poisson disk on average, but with clumps and gaps
    [Serializable]
    public class RandomSampler : ISurfaceSampler
    {
        [Tooltip("Mean distance between splats in metres: one splat per spacing squared of surface.")]
        [Min(0.0001f)] public float spacing = 0.0045f;
        public int seed = 1;

        public List<SurfaceSample> Sample(IReadOnlyList<SurfaceMesh> meshes)
        {
            var result = new List<SurfaceSample>();
            for (int m = 0; m < meshes.Count; m++)
            {
                SurfaceMesh mesh = meshes[m];
                if (mesh.triangles.Length == 0)
                    continue;
                var cumulativeArea = new float[mesh.triangles.Length];
                double total = 0;
                for (int t = 0; t < mesh.triangles.Length; t++)
                {
                    total += mesh.TriangleArea(t);
                    cumulativeArea[t] = (float)total;
                }
                long count = (long)Math.Round(total / (spacing * spacing));
                if (count > int.MaxValue)
                    throw new InvalidOperationException($"{mesh.path}: too many samples at {spacing * 1000f:F2} mm; raise the spacing");

                uint hash = math.hash(new int2(seed, m));
                var rng = new Random(hash == 0 ? 1u : hash);
                for (long i = 0; i < count; i++)
                {
                    int t = Array.BinarySearch(cumulativeArea, rng.NextFloat() * (float)total);
                    t = t < 0 ? Math.Min(~t, cumulativeArea.Length - 1) : t;
                    float r1 = math.sqrt(rng.NextFloat());
                    float r2 = rng.NextFloat();
                    result.Add(new SurfaceSample
                    {
                        mesh = m,
                        triangle = t,
                        bary = new float3(1f - r1, r1 * (1f - r2), r1 * r2),
                        spacing = spacing,
                    });
                }
            }
            return result;
        }
    }
}
