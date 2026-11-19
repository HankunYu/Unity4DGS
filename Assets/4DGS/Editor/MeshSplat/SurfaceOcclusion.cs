using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Ambient occlusion of the samples: how much of the hemisphere around
    // each one's normal the model itself closes off, so folds, armpits and
    // the hollows under a chin or a collar come out dark and exposed surfaces
    // light. Stored per splat next to the binding, for styles to use as a
    // mask (GaussianSplatStyle's occlusion). It is judged in the sampled pose
    // (the bind pose for skinned meshes) and does not follow the animation: a
    // hand closing in on the body later darkens nothing.
    [Serializable]
    public class SurfaceOcclusion
    {
        [Tooltip("Bake how much the model closes off each splat's surroundings, for styles to darken creases and hollows by")]
        public bool enabled;
        [Tooltip("Rays cast from each sample over the hemisphere around its normal; more gives smoother steps between neighbours")]
        [Range(8, 128)] public int rayCount = 48;
        [Tooltip("How far the model around a sample still counts, in metres. Cover nearer by counts more. Short reaches pick out creases, long ones shade whole hollows")]
        [Min(0.001f)] public float distance = 0.3f;
        [Tooltip("Only a sample's own mesh can cover it. Skinned meshes overlap in their bind pose (characters standing at the origin together), where other meshes would cover them wrongly")]
        public bool ownMeshOnly = true;

        // How open each sample is: 0 fully covered, 1 nothing near it. Null
        // when not enabled.
        public float[] Bake(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples)
        {
            if (!enabled)
                return null;

            var openness = new float[samples.Count];
            Array.Fill(openness, 1f);
            SurfaceRays.ForEachCover(meshes, samples, ownMeshOnly,
                (cover, indices) => Measure(cover, meshes, samples, indices, openness));
            return openness;
        }

        private void Measure(IReadOnlyList<SurfaceMesh> cover, IReadOnlyList<SurfaceMesh> meshes,
            IReadOnlyList<SurfaceSample> samples, List<int> indices, float[] openness)
        {
            using var grid = TriangleGrid.Build(cover);
            if (grid.triangleCount == 0)
                return;
            var origins = new NativeArray<float3>(indices.Count, Allocator.TempJob);
            var normals = new NativeArray<float3>(indices.Count, Allocator.TempJob);
            var result = new NativeArray<float>(indices.Count, Allocator.TempJob);
            try
            {
                SurfaceRays.Fill(meshes, samples, indices, origins, normals);
                new MeasureJob
                {
                    grid = grid,
                    origins = origins,
                    normals = normals,
                    rayCount = rayCount,
                    distance = distance,
                    openness = result,
                }.Schedule(indices.Count, 256).Complete();
                for (int k = 0; k < indices.Count; k++)
                    openness[indices[k]] = result[k];
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
        private struct MeasureJob : IJobParallelFor
        {
            [ReadOnly] public TriangleGrid grid;
            [ReadOnly] public NativeArray<float3> origins;
            [ReadOnly] public NativeArray<float3> normals;
            public int rayCount;
            public float distance;
            [WriteOnly] public NativeArray<float> openness;

            public void Execute(int index)
            {
                float3 n = normals[index];
                SurfaceRays.Frame(n, out float3 tangent, out float3 bitangent);
                var random = SurfaceRays.Seed(index, out float turn);
                // The directions are cosine weighted already, so the rays
                // count alike; a hit covers less the farther away it is
                float covered = 0;
                for (int k = 0; k < rayCount; k++)
                {
                    float3 dir = SurfaceRays.Direction(n, tangent, bitangent, k, rayCount, turn, ref random);
                    grid.Raycast(origins[index], dir, distance, out int triangle, out _, out float hit);
                    if (triangle >= 0)
                        covered += 1f - hit / distance;
                }
                openness[index] = 1f - covered / rayCount;
            }
        }
    }
}
