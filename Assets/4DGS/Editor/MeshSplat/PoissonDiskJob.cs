using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Poisson disk sampling of one mesh: random candidates, each kept only if
    // no kept sample is closer than the smaller of their two radii. Every
    // triangle has its radius (spacing times its radius scale, at most 1),
    // and gets candidates in proportion to its area over radius squared, so
    // denser regions fill as completely as sparse ones.
    internal static class PoissonDiskJob
    {
        // Candidates thrown per radius^2 of area. Coverage saturates around
        // 0.44 samples per radius^2, so more mostly costs time.
        private const float CandidateDensity = 4f;

        // radiusScale: per triangle in (0, 1], or null for a uniform radius
        public static void Sample(SurfaceMesh mesh, int meshIndex, float spacing, float[] radiusScale, int seed,
            List<SurfaceSample> result)
        {
            int triangleCount = mesh.triangles.Length;
            if (triangleCount == 0)
                return;
            var scales = new NativeArray<float>(triangleCount, Allocator.TempJob);
            var cumulativeWeight = new NativeArray<float>(triangleCount, Allocator.TempJob);
            double total = 0;
            float minScale = 1f;
            for (int t = 0; t < triangleCount; t++)
            {
                float scale = radiusScale != null ? math.clamp(radiusScale[t], 0.05f, 1f) : 1f;
                scales[t] = scale;
                minScale = math.min(minScale, scale);
                total += mesh.TriangleArea(t) / (scale * scale);
                cumulativeWeight[t] = (float)total;
            }
            long candidates = (long)Math.Ceiling(CandidateDensity * total / (spacing * spacing));
            if (candidates > int.MaxValue)
            {
                scales.Dispose();
                cumulativeWeight.Dispose();
                throw new InvalidOperationException(
                    $"{mesh.path}: {spacing * 1000f:F2} mm spacing needs too many candidates; raise the spacing");
            }

            var positions = new NativeArray<float3>(mesh.positions, Allocator.TempJob);
            var triangles = new NativeArray<int3>(mesh.triangles, Allocator.TempJob);
            int expected = (int)Math.Min(candidates / 8 + 16, int.MaxValue);
            var samples = new NativeList<SurfaceSample>(expected, Allocator.TempJob);
            var kept = new NativeList<float4>(expected, Allocator.TempJob);
            var next = new NativeList<int>(expected, Allocator.TempJob);
            var cellHeads = new NativeHashMap<long, int>(expected, Allocator.TempJob);
            try
            {
                uint hash = math.hash(new int2(seed, meshIndex));
                new SampleJob
                {
                    positions = positions,
                    triangles = triangles,
                    radiusScales = scales,
                    cumulativeWeight = cumulativeWeight,
                    meshIndex = meshIndex,
                    spacing = spacing,
                    cellSize = spacing * minScale,
                    candidates = (int)candidates,
                    seed = hash == 0 ? 1u : hash,
                    samples = samples,
                    kept = kept,
                    next = next,
                    cellHeads = cellHeads,
                }.Run();

                // Area per sample is radius^2 times this, wherever the radius
                // differs, so the local spacing follows the local radius
                float spacingPerRadius = samples.Length > 0 ? (float)Math.Sqrt(total / samples.Length) / spacing : 0f;
                foreach (SurfaceSample sample in samples)
                {
                    SurfaceSample s = sample;
                    s.spacing = spacing * scales[s.triangle] * spacingPerRadius;
                    result.Add(s);
                }
            }
            finally
            {
                scales.Dispose();
                cumulativeWeight.Dispose();
                positions.Dispose();
                triangles.Dispose();
                samples.Dispose();
                kept.Dispose();
                next.Dispose();
                cellHeads.Dispose();
            }
        }

        // Synchronous, or the editor runs it as plain C# until Burst is ready
        [BurstCompile(CompileSynchronously = true)]
        private struct SampleJob : IJob
        {
            [ReadOnly] public NativeArray<float3> positions;
            [ReadOnly] public NativeArray<int3> triangles;
            [ReadOnly] public NativeArray<float> radiusScales;
            // Inclusive prefix sums of area / radiusScale^2
            [ReadOnly] public NativeArray<float> cumulativeWeight;
            public int meshIndex;
            public float spacing;
            // The smallest radius: a candidate searches the cells within its
            // own radius, which holds every sample it can conflict with
            public float cellSize;
            public int candidates;
            public uint seed;

            public NativeList<SurfaceSample> samples;
            // Kept positions (xyz) and radii (w), chained per grid cell
            public NativeList<float4> kept;
            public NativeList<int> next;
            public NativeHashMap<long, int> cellHeads;

            public void Execute()
            {
                var rng = new Random(seed);
                float total = cumulativeWeight[cumulativeWeight.Length - 1];
                float invCell = 1f / cellSize;

                // Candidates are independent draws, so their order is already random
                for (int i = 0; i < candidates; i++)
                {
                    int t = FindTriangle(rng.NextFloat() * total);
                    float r1 = math.sqrt(rng.NextFloat());
                    float r2 = rng.NextFloat();
                    var bary = new float3(1f - r1, r1 * (1f - r2), r1 * r2);
                    int3 tri = triangles[t];
                    float3 p = bary.x * positions[tri.x] + bary.y * positions[tri.y] + bary.z * positions[tri.z];
                    float radius = spacing * radiusScales[t];
                    var cell = (int3)math.floor(p * invCell);

                    if (HasNeighbor(p, radius, cell))
                        continue;

                    long key = CellKey(cell);
                    next.Add(cellHeads.TryGetValue(key, out int head) ? head : -1);
                    cellHeads[key] = kept.Length;
                    kept.Add(new float4(p, radius));
                    samples.Add(new SurfaceSample { mesh = meshIndex, triangle = t, bary = bary });
                }
            }

            private int FindTriangle(float weight)
            {
                int lo = 0, hi = cumulativeWeight.Length - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi) >> 1;
                    if (cumulativeWeight[mid] < weight)
                        lo = mid + 1;
                    else
                        hi = mid;
                }
                return lo;
            }

            private bool HasNeighbor(float3 p, float radius, int3 cell)
            {
                int reach = (int)math.ceil(radius / cellSize - 1e-4f);
                for (int z = -reach; z <= reach; z++)
                for (int y = -reach; y <= reach; y++)
                for (int x = -reach; x <= reach; x++)
                {
                    if (!cellHeads.TryGetValue(CellKey(cell + new int3(x, y, z)), out int k))
                        continue;
                    for (; k >= 0; k = next[k])
                    {
                        float4 q = kept[k];
                        float minDist = math.min(radius, q.w);
                        if (math.distancesq(p, q.xyz) < minDist * minDist)
                            return true;
                    }
                }
                return false;
            }

            // 21 bits per axis: about +-3 km at 3 mm spacing
            private static long CellKey(int3 c)
            {
                return ((long)(c.x & 0x1FFFFF) << 42) | ((long)(c.y & 0x1FFFFF) << 21) | (long)(c.z & 0x1FFFFF);
            }
        }
    }
}
