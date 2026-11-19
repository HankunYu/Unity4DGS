using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Drops samples on surfaces the model covers itself: the inner layer of
    // double-sided clothing, a waistband under a jacket, skin under a sleeve.
    // Rays leave each sample over the hemisphere around its normal; a sample
    // whose rays nearly all hit the model again within a short distance cannot
    // be seen from outside. It is judged in the sampled pose (the bind pose for
    // skinned meshes), so a surface covered there but uncovered by the
    // animation shows a gap.
    [Serializable]
    public class HiddenSurfaceFilter
    {
        [Tooltip("Remove samples the model covers itself, e.g. clothing under other clothing")]
        public bool enabled;
        [Tooltip("Rays cast from each sample over the hemisphere around its normal")]
        [Range(4, 64)] public int rayCount = 24;
        [Tooltip("Only cover nearer than this, in metres, hides a sample, so concave parts of the outer surface (folds, armpits) stay")]
        [Min(0.001f)] public float distance = 0.05f;
        [Tooltip("A sample is removed when at least this fraction of its rays is blocked")]
        [Range(0.5f, 1f)] public float hiddenFraction = 0.9f;
        [Tooltip("Only a sample's own mesh can hide it. Skinned meshes overlap in their bind pose (characters standing at the origin together), where other meshes would hide them wrongly")]
        public bool ownMeshOnly = true;

        public List<SurfaceSample> Apply(IReadOnlyList<SurfaceMesh> meshes, List<SurfaceSample> samples)
        {
            if (!enabled || samples.Count == 0)
                return samples;

            var hidden = new bool[samples.Count];
            SurfaceRays.ForEachCover(meshes, samples, ownMeshOnly,
                (cover, indices) => MarkHidden(cover, meshes, samples, indices, hidden));

            var kept = new List<SurfaceSample>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                if (!hidden[i])
                    kept.Add(samples[i]);
            }
            return kept;
        }

        // Tests the samples at indices against the cover meshes
        private void MarkHidden(IReadOnlyList<SurfaceMesh> cover, IReadOnlyList<SurfaceMesh> meshes,
            List<SurfaceSample> samples, List<int> indices, bool[] hidden)
        {
            using var grid = TriangleGrid.Build(cover);
            if (grid.triangleCount == 0)
                return;
            var origins = new NativeArray<float3>(indices.Count, Allocator.TempJob);
            var normals = new NativeArray<float3>(indices.Count, Allocator.TempJob);
            var result = new NativeArray<bool>(indices.Count, Allocator.TempJob);
            try
            {
                SurfaceRays.Fill(meshes, samples, indices, origins, normals);
                new TestJob
                {
                    grid = grid,
                    origins = origins,
                    normals = normals,
                    rayCount = rayCount,
                    distance = distance,
                    maxOpen = (int)math.floor(rayCount * (1f - hiddenFraction) + 1e-4f),
                    hidden = result,
                }.Schedule(indices.Count, 256).Complete();
                for (int k = 0; k < indices.Count; k++)
                    hidden[indices[k]] = result[k];
            }
            finally
            {
                origins.Dispose();
                normals.Dispose();
                result.Dispose();
            }
        }

        // Synchronous, or the editor runs it as plain C# until Burst is ready
        [BurstCompile(CompileSynchronously = true)]
        private struct TestJob : IJobParallelFor
        {
            [ReadOnly] public TriangleGrid grid;
            [ReadOnly] public NativeArray<float3> origins;
            [ReadOnly] public NativeArray<float3> normals;
            public int rayCount;
            public float distance;
            // More rays than this getting out means the sample is visible
            public int maxOpen;
            [WriteOnly] public NativeArray<bool> hidden;

            public void Execute(int index)
            {
                float3 n = normals[index];
                SurfaceRays.Frame(n, out float3 tangent, out float3 bitangent);
                var random = SurfaceRays.Seed(index, out float turn);
                int open = 0;
                for (int k = 0; k < rayCount; k++)
                {
                    float3 dir = SurfaceRays.Direction(n, tangent, bitangent, k, rayCount, turn, ref random);
                    grid.Raycast(origins[index], dir, distance, out int triangle, out _, out _);
                    if (triangle < 0 && ++open > maxOpen)
                    {
                        hidden[index] = false;
                        return;
                    }
                }
                hidden[index] = true;
            }
        }
    }
}
