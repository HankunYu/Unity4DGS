using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Samples the way a spinning LiDAR sees the model: each scanner casts
    // rings of rays around its own up axis, a fixed angle apart, with points
    // a fixed angle apart along each ring, and keeps the first surface each
    // ray hits. That gives visible scan lines, density falling off with
    // distance, and nothing where every scanner is blocked. The scanners
    // live in model space, so they move with the model: the samples stay put
    // on the surface when it animates.
    [Serializable]
    public class ScanlineSampler : ISurfaceSampler
    {
        [Serializable]
        public struct Scanner
        {
            // Model space; scan lines are rings around the rotated local Y axis
            public Vector3 position;
            public Vector3 rotation;
        }

        private const long MaxRays = 200_000_000;

        [Tooltip("Scanner positions and orientations in model space. Scan lines are rings around each scanner's local Y axis; add scanners to reach the sides one cannot see.")]
        public Scanner[] scanners = { new() { position = new Vector3(0f, 1.5f, 3f) } };
        [Tooltip("Angle between scan lines, in degrees.")]
        [Range(0.01f, 10f)] public float lineSpacing = 0.5f;
        [Tooltip("Angle between points along a scan line, in degrees.")]
        [Range(0.001f, 1f)] public float pointSpacing = 0.05f;
        [Tooltip("Rays stop this far from the scanner, in metres.")]
        [Min(0.01f)] public float maxRange = 100f;

        public List<SurfaceSample> Sample(IReadOnlyList<SurfaceMesh> meshes)
        {
            var result = new List<SurfaceSample>();
            if (scanners == null || scanners.Length == 0)
                return result;
            using var grid = TriangleGrid.Build(meshes);
            if (grid.triangleCount == 0)
                return result;

            float lineStep = math.radians(lineSpacing);
            float pointStep = math.radians(pointSpacing);
            foreach (Scanner scanner in scanners)
            {
                quaternion rotation = quaternion.Euler(math.radians(scanner.rotation));
                float3 origin = scanner.position;
                AngularBounds(grid.boundsMin, grid.boundsMax, origin, rotation,
                    out float azimuthCenter, out float2 azimuthRange, out float2 elevationRange);
                int lines = (int)math.floor((elevationRange.y - elevationRange.x) / lineStep) + 1;
                int points = (int)math.floor((azimuthRange.y - azimuthRange.x) / pointStep) + 1;
                if ((long)lines * points > MaxRays)
                    throw new InvalidOperationException($"{(long)lines * points:N0} rays per scanner; raise the line or point spacing");

                var hits = new NativeArray<Hit>(lines * points, Allocator.TempJob);
                try
                {
                    new CastJob
                    {
                        grid = grid,
                        origin = origin,
                        rotation = rotation,
                        azimuthStart = azimuthCenter + azimuthRange.x,
                        elevationStart = elevationRange.x,
                        lineStep = lineStep,
                        pointStep = pointStep,
                        pointsPerLine = points,
                        maxRange = maxRange,
                        hits = hits,
                    }.Schedule(hits.Length, 1024).Complete();

                    foreach (Hit hit in hits)
                    {
                        if (hit.triangle < 0)
                            continue;
                        int2 owner = grid.owners[hit.triangle];
                        result.Add(new SurfaceSample
                        {
                            mesh = owner.x,
                            triangle = owner.y,
                            bary = new float3(1f - hit.uv.x - hit.uv.y, hit.uv.x, hit.uv.y),
                            // Along the scan line; lines are further apart
                            spacing = hit.distance * pointStep,
                        });
                    }
                }
                finally
                {
                    hits.Dispose();
                }
            }
            return result;
        }

        // The model's azimuth and elevation extent as seen from the scanner,
        // so rays that cannot hit it are never cast. Azimuth is relative to
        // the direction of the bounds' center.
        private static void AngularBounds(float3 min, float3 max, float3 origin, quaternion rotation,
            out float azimuthCenter, out float2 azimuthRange, out float2 elevationRange)
        {
            quaternion toScanner = math.inverse(rotation);
            float3 center = math.rotate(toScanner, (min + max) * 0.5f - origin);
            azimuthCenter = math.atan2(center.x, center.z);
            bool inside = math.all(origin >= min) && math.all(origin <= max);
            azimuthRange = new float2(-math.PI, math.PI);
            elevationRange = new float2(-math.PI * 0.5f, math.PI * 0.5f);
            if (inside)
                return;

            var az = new float2(float.MaxValue, float.MinValue);
            var el = new float2(float.MaxValue, float.MinValue);
            for (int c = 0; c < 8; c++)
            {
                float3 corner = new float3((c & 1) != 0 ? max.x : min.x, (c & 2) != 0 ? max.y : min.y, (c & 4) != 0 ? max.z : min.z);
                float3 d = math.rotate(toScanner, corner - origin);
                float a = math.atan2(d.x, d.z) - azimuthCenter;
                a = math.atan2(math.sin(a), math.cos(a));
                float e = math.atan2(d.y, math.length(d.xz));
                az = new float2(math.min(az.x, a), math.max(az.y, a));
                el = new float2(math.min(el.x, e), math.max(el.y, e));
            }
            // Corners can wrap behind a scanner that is close to the box
            if (az.y - az.x < math.PI)
                azimuthRange = az;
            elevationRange = el;
        }

        private struct Hit
        {
            public int triangle;
            public float2 uv;
            public float distance;
        }

        // Synchronous, or the editor runs it as plain C# until Burst is ready
        [BurstCompile(CompileSynchronously = true)]
        private struct CastJob : IJobParallelFor
        {
            [ReadOnly] public TriangleGrid grid;
            public float3 origin;
            public quaternion rotation;
            public float azimuthStart;
            public float elevationStart;
            public float lineStep;
            public float pointStep;
            public int pointsPerLine;
            public float maxRange;
            [WriteOnly] public NativeArray<Hit> hits;

            public void Execute(int index)
            {
                float elevation = elevationStart + (index / pointsPerLine) * lineStep;
                float azimuth = azimuthStart + (index % pointsPerLine) * pointStep;
                math.sincos(elevation, out float sinEl, out float cosEl);
                math.sincos(azimuth, out float sinAz, out float cosAz);
                float3 dir = math.rotate(rotation, new float3(cosEl * sinAz, sinEl, cosEl * cosAz));
                grid.Raycast(origin, dir, maxRange, out int triangle, out float2 uv, out float distance);
                hits[index] = new Hit { triangle = triangle, uv = uv, distance = distance };
            }
        }
    }
}
