using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // One sample on each vertex, so the density follows the mesh's own
    // tessellation. Vertices split at UV or normal seams share a position
    // and are merged into one sample.
    [Serializable]
    public class VertexSampler : ISurfaceSampler
    {
        [Tooltip("Vertices closer than this (metres) become one splat. 0 keeps every vertex, seams included.")]
        [Min(0f)] public float mergeDistance = 0.0001f;

        public List<SurfaceSample> Sample(IReadOnlyList<SurfaceMesh> meshes)
        {
            var result = new List<SurfaceSample>();
            for (int m = 0; m < meshes.Count; m++)
            {
                SurfaceMesh mesh = meshes[m];
                // A triangle using each vertex, and which corner it is there
                var corner = new int2[mesh.positions.Length];
                for (int v = 0; v < corner.Length; v++)
                    corner[v] = new int2(-1, 0);
                for (int t = 0; t < mesh.triangles.Length; t++)
                {
                    int3 tri = mesh.triangles[t];
                    for (int c = 0; c < 3; c++)
                    {
                        if (corner[tri[c]].x < 0)
                            corner[tri[c]] = new int2(t, c);
                    }
                }

                var seen = new HashSet<int3>();
                float invMerge = mergeDistance > 0f ? 1f / mergeDistance : 0f;
                int first = result.Count;
                for (int v = 0; v < corner.Length; v++)
                {
                    if (corner[v].x < 0)
                        continue;
                    if (invMerge > 0f && !seen.Add((int3)math.round(mesh.positions[v] * invMerge)))
                        continue;
                    var bary = float3.zero;
                    bary[corner[v].y] = 1f;
                    result.Add(new SurfaceSample { mesh = m, triangle = corner[v].x, bary = bary });
                }

                int count = result.Count - first;
                float spacing = count > 0 ? math.sqrt(mesh.Area() / count) : 0f;
                for (int i = first; i < result.Count; i++)
                {
                    SurfaceSample s = result[i];
                    s.spacing = spacing;
                    result[i] = s;
                }
            }
            return result;
        }
    }
}
