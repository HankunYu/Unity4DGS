using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace GaussianSplatting.Editor.MeshSplat
{
    // All triangles of a model binned into a uniform grid, for casting many
    // rays from Burst jobs: a ray walks the cells it crosses (3D DDA) and
    // stops at the first cell that contains its nearest hit.
    internal struct TriangleGrid : IDisposable
    {
        // About this many cells per triangle
        private const float CellsPerTriangle = 2f;
        private const int MaxCells = 1 << 24;

        // 3 corners per triangle, model space
        [ReadOnly] public NativeArray<float3> corners;
        // Per triangle: mesh index, triangle index in that mesh
        [ReadOnly] public NativeArray<int2> owners;
        // Triangles of cell c are cellTriangles[cellStart[c] .. cellStart[c + 1] - 1]
        [ReadOnly] public NativeArray<int> cellStart;
        [ReadOnly] public NativeArray<int> cellTriangles;
        public float3 boundsMin;
        public float3 boundsMax;
        public float3 cellSize;
        public int3 dims;
        public int triangleCount;

        public static TriangleGrid Build(IReadOnlyList<SurfaceMesh> meshes)
        {
            int count = 0;
            foreach (SurfaceMesh mesh in meshes)
                count += mesh.triangles.Length;
            var grid = new TriangleGrid
            {
                corners = new NativeArray<float3>(count * 3, Allocator.TempJob),
                owners = new NativeArray<int2>(count, Allocator.TempJob),
                triangleCount = count,
                boundsMin = new float3(float.MaxValue),
                boundsMax = new float3(float.MinValue),
            };
            int t = 0;
            for (int m = 0; m < meshes.Count; m++)
            {
                SurfaceMesh mesh = meshes[m];
                for (int i = 0; i < mesh.triangles.Length; i++, t++)
                {
                    int3 tri = mesh.triangles[i];
                    for (int c = 0; c < 3; c++)
                    {
                        float3 p = mesh.positions[tri[c]];
                        grid.corners[t * 3 + c] = p;
                        grid.boundsMin = math.min(grid.boundsMin, p);
                        grid.boundsMax = math.max(grid.boundsMax, p);
                    }
                    grid.owners[t] = new int2(m, i);
                }
            }
            if (count == 0)
            {
                grid.cellStart = new NativeArray<int>(1, Allocator.TempJob);
                grid.cellTriangles = new NativeArray<int>(0, Allocator.TempJob);
                return grid;
            }

            float3 extent = math.max(grid.boundsMax - grid.boundsMin, 1e-4f);
            float targetCells = math.min(count * CellsPerTriangle, MaxCells);
            float size = math.pow(extent.x * extent.y * extent.z / targetCells, 1f / 3f);
            grid.dims = math.clamp((int3)math.ceil(extent / size), 1, 1024);
            grid.cellSize = extent / grid.dims;
            grid.boundsMax = grid.boundsMin + extent;

            // Counting pass, then fill: each triangle goes into every cell
            // its bounding box touches
            int cellCount = grid.dims.x * grid.dims.y * grid.dims.z;
            var start = new int[cellCount + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                int[] fill = pass == 1 ? (int[])start.Clone() : null;
                if (pass == 1)
                    grid.cellTriangles = new NativeArray<int>(start[cellCount], Allocator.TempJob);
                for (int i = 0; i < count; i++)
                {
                    float3 a = grid.corners[i * 3], b = grid.corners[i * 3 + 1], c = grid.corners[i * 3 + 2];
                    int3 lo = grid.CellOf(math.min(a, math.min(b, c)));
                    int3 hi = grid.CellOf(math.max(a, math.max(b, c)));
                    for (int z = lo.z; z <= hi.z; z++)
                    for (int y = lo.y; y <= hi.y; y++)
                    for (int x = lo.x; x <= hi.x; x++)
                    {
                        int cell = grid.CellIndex(new int3(x, y, z));
                        if (pass == 0)
                            start[cell + 1]++;
                        else
                            grid.cellTriangles[fill[cell]++] = i;
                    }
                }
                if (pass == 0)
                {
                    for (int c = 0; c < cellCount; c++)
                        start[c + 1] += start[c];
                }
            }
            grid.cellStart = new NativeArray<int>(start, Allocator.TempJob);
            return grid;
        }

        public void Dispose()
        {
            if (corners.IsCreated) corners.Dispose();
            if (owners.IsCreated) owners.Dispose();
            if (cellStart.IsCreated) cellStart.Dispose();
            if (cellTriangles.IsCreated) cellTriangles.Dispose();
        }

        private int3 CellOf(float3 p) => math.clamp((int3)math.floor((p - boundsMin) / cellSize), 0, dims - 1);

        private int CellIndex(int3 c) => (c.z * dims.y + c.y) * dims.x + c.x;

        // Nearest hit within maxDistance: triangle index (-1 for none), the
        // barycentric weights of its second and third corners, and distance
        public void Raycast(float3 origin, float3 dir, float maxDistance, out int triangle, out float2 uv, out float distance)
        {
            triangle = -1;
            uv = 0;
            distance = maxDistance;

            float3 invDir = 1f / dir;
            float3 t0 = (boundsMin - origin) * invDir;
            float3 t1 = (boundsMax - origin) * invDir;
            float tEnter = math.max(math.cmax(math.min(t0, t1)), 0f);
            float tExit = math.min(math.cmin(math.max(t0, t1)), maxDistance);
            if (tEnter > tExit)
                return;

            int3 cell = CellOf(origin + dir * tEnter);
            int3 step = (int3)math.sign(dir);
            float3 nextBoundary = boundsMin + (cell + math.max(step, 0)) * cellSize;
            float3 tMax = math.select((nextBoundary - origin) * invDir, float.MaxValue, step == 0);
            float3 tDelta = math.select(cellSize * math.abs(invDir), float.MaxValue, step == 0);

            while (true)
            {
                int c = CellIndex(cell);
                for (int k = cellStart[c]; k < cellStart[c + 1]; k++)
                {
                    int i = cellTriangles[k];
                    if (Intersect(origin, dir, corners[i * 3], corners[i * 3 + 1], corners[i * 3 + 2], out float t, out float2 hitUv) &&
                        t < distance)
                    {
                        distance = t;
                        triangle = i;
                        uv = hitUv;
                    }
                }
                float cellExit = math.cmin(tMax);
                // A hit inside this cell beats anything further along
                if (triangle >= 0 && distance <= cellExit)
                    return;
                if (cellExit > tExit)
                    return;
                if (tMax.x <= tMax.y && tMax.x <= tMax.z)
                {
                    cell.x += step.x;
                    tMax.x += tDelta.x;
                }
                else if (tMax.y <= tMax.z)
                {
                    cell.y += step.y;
                    tMax.y += tDelta.y;
                }
                else
                {
                    cell.z += step.z;
                    tMax.z += tDelta.z;
                }
                if (math.any(cell < 0) || math.any(cell >= dims))
                    return;
            }
        }

        // Möller-Trumbore, both faces
        private static bool Intersect(float3 o, float3 d, float3 a, float3 b, float3 c, out float t, out float2 uv)
        {
            t = 0;
            uv = 0;
            float3 e1 = b - a, e2 = c - a;
            float3 p = math.cross(d, e2);
            float det = math.dot(e1, p);
            if (math.abs(det) < 1e-12f)
                return false;
            float inv = 1f / det;
            float3 s = o - a;
            float u = math.dot(s, p) * inv;
            if (u < 0f || u > 1f)
                return false;
            float3 q = math.cross(s, e1);
            float v = math.dot(d, q) * inv;
            if (v < 0f || u + v > 1f)
                return false;
            t = math.dot(e2, q) * inv;
            uv = new float2(u, v);
            return t > 1e-6f;
        }
    }
}
