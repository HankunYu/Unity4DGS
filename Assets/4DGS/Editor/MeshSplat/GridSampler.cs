using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Jobs;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Points of a regular 3D lattice, projected onto the surface: each
    // triangle takes the lattice lines along the axis its normal is closest
    // to, and gets a sample wherever one passes through it. Surfaces facing
    // an axis show an exact square grid, slanted ones stretched rows, and
    // there is a seam where the facing axis changes. The lattice lives in
    // model space, so it moves with the model like the scanners do.
    [Serializable]
    public class GridSampler : ISurfaceSampler
    {
        private const long MaxSamples = 200_000_000;
        // In lattice units: samples closer than this are the same point
        private const float MergeDistance = 1e-4f;

        [Tooltip("Distance between lattice lines, in metres.")]
        [Min(0.0001f)] public float spacing = 0.005f;
        [Tooltip("Orientation of the lattice in model space, in degrees.")]
        public Vector3 rotation;
        [Tooltip("Position of a lattice point in model space, in metres: shifts where the grid lines fall.")]
        public Vector3 offset;

        public List<SurfaceSample> Sample(IReadOnlyList<SurfaceMesh> meshes)
        {
            var result = new List<SurfaceSample>();
            var toGrid = math.inverse(quaternion.Euler(math.radians((float3)rotation)));
            for (int m = 0; m < meshes.Count; m++)
            {
                SurfaceMesh mesh = meshes[m];
                if (mesh.triangles.Length == 0)
                    continue;
                // Facing the projection axis, a triangle gets one sample per
                // spacing^2; slanted ones fewer, never more
                if (mesh.Area() / (spacing * spacing) > MaxSamples)
                    throw new InvalidOperationException($"{mesh.path}: too many samples at {spacing * 1000f:F2} mm; raise the spacing");

                var positions = new NativeArray<float3>(mesh.positions.Length, Allocator.TempJob);
                for (int v = 0; v < positions.Length; v++)
                    positions[v] = math.rotate(toGrid, mesh.positions[v] - (float3)offset) / spacing;
                var triangles = new NativeArray<int3>(mesh.triangles, Allocator.TempJob);
                var samples = new NativeList<SurfaceSample>(Allocator.TempJob);
                var taken = new NativeParallelMultiHashMap<int3, float3>(1024, Allocator.TempJob);
                try
                {
                    new ProjectJob
                    {
                        positions = positions,
                        triangles = triangles,
                        meshIndex = m,
                        spacing = spacing,
                        samples = samples,
                        taken = taken,
                    }.Run();
                    foreach (SurfaceSample sample in samples)
                        result.Add(sample);
                }
                finally
                {
                    positions.Dispose();
                    triangles.Dispose();
                    samples.Dispose();
                    taken.Dispose();
                }
            }
            return result;
        }

        // Synchronous, or the editor runs it as plain C# until Burst is ready
        [BurstCompile(CompileSynchronously = true)]
        private struct ProjectJob : IJob
        {
            // In lattice units: lattice points are at integer coordinates
            [ReadOnly] public NativeArray<float3> positions;
            [ReadOnly] public NativeArray<int3> triangles;
            public int meshIndex;
            public float spacing;
            public NativeList<SurfaceSample> samples;
            // Kept sample positions by their nearest 3D lattice point. A point
            // on a shared edge is inside every triangle touching it, as is one
            // where faces projecting along different axes meet (a box edge);
            // it is kept once. Two of its coordinates are lattice lines, so
            // copies of it never round to different keys.
            public NativeParallelMultiHashMap<int3, float3> taken;

            public void Execute()
            {
                for (int t = 0; t < triangles.Length; t++)
                {
                    int3 tri = triangles[t];
                    float3 a = positions[tri.x], b = positions[tri.y], c = positions[tri.z];
                    float3 normal = math.cross(b - a, c - a);
                    float3 absNormal = math.abs(normal);
                    float normalLength = math.length(normal);
                    if (normalLength <= 0f)
                        continue;
                    int axis = absNormal.x >= absNormal.y && absNormal.x >= absNormal.z ? 0 : absNormal.y >= absNormal.z ? 1 : 2;
                    float2 pa = Project(a, axis), pb = Project(b, axis), pc = Project(c, axis);
                    // Area per sample grows as the triangle tilts away from the axis
                    float sampleSpacing = spacing * math.rsqrt(absNormal[axis] / normalLength);

                    // Counter-clockwise in the projection, remembering which
                    // corner went where for the barycentric weights
                    bool swapped = Cross(pb - pa, pc - pa) < 0f;
                    if (swapped)
                        (pb, pc) = (pc, pb);

                    float2 min = math.min(pa, math.min(pb, pc));
                    float2 max = math.max(pa, math.max(pb, pc));
                    int2 first = (int2)math.ceil(min);
                    int2 last = (int2)math.floor(max);
                    for (int y = first.y; y <= last.y; y++)
                    for (int x = first.x; x <= last.x; x++)
                    {
                        var p = new float2(x, y);
                        if (!Inside(pb, pc, p, out float wa) || !Inside(pc, pa, p, out float wb) || !Inside(pa, pb, p, out float wc))
                            continue;
                        float sum = wa + wb + wc;
                        if (sum <= 0f)
                            continue;
                        float3 bary = new float3(wa, wb, wc) / sum;
                        if (swapped)
                            bary = bary.xzy;
                        float3 q = bary.x * a + bary.y * b + bary.z * c;
                        var key = (int3)math.round(q);
                        if (IsTaken(key, q))
                            continue;
                        taken.Add(key, q);
                        samples.Add(new SurfaceSample
                        {
                            mesh = meshIndex,
                            triangle = t,
                            bary = bary,
                            spacing = sampleSpacing,
                        });
                    }
                }
            }

            private bool IsTaken(int3 key, float3 q)
            {
                if (!taken.TryGetFirstValue(key, out float3 other, out var it))
                    return false;
                do
                {
                    if (math.distancesq(q, other) < MergeDistance * MergeDistance)
                        return true;
                }
                while (taken.TryGetNextValue(out other, ref it));
                return false;
            }

            private static float2 Project(float3 p, int axis) => axis switch
            {
                0 => p.yz,
                1 => p.zx,
                _ => p.xy,
            };

            private static float Cross(float2 u, float2 v) => u.x * v.y - u.y * v.x;

            // Whether p is on or inside the edge from -> to (on its left).
            // The edge function is evaluated from the edge's lower endpoint
            // either way round, so a triangle and its neighbour get exactly
            // opposite values and a point on their shared edge is inside at
            // least one of them.
            private static bool Inside(float2 from, float2 to, float2 p, out float weight)
            {
                bool forward = from.y < to.y || (from.y == to.y && from.x < to.x);
                float2 lo = forward ? from : to;
                float2 hi = forward ? to : from;
                float w = Cross(hi - lo, p - lo);
                weight = forward ? w : -w;
                return weight >= 0f;
            }
        }
    }
}
