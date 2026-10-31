using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Poisson disk sampling that gets denser along creases, sharp curvature
    // and open borders, so edges and silhouettes read crisply while flat
    // areas stay sparse. A triangle's feature strength is the sharpest bend
    // across its edges (an open edge counts as fully sharp); its spacing
    // goes from spacing (flat) to featureSpacing (bend of featureAngle or
    // more).
    [Serializable]
    public class FeatureSampler : ISurfaceSampler
    {
        [Tooltip("Minimum distance between splats on flat areas, in metres.")]
        [Min(0.0001f)] public float spacing = 0.006f;
        [Tooltip("Minimum distance between splats on the sharpest creases and open borders, in metres (at most Spacing).")]
        [Min(0.0001f)] public float featureSpacing = 0.002f;
        [Tooltip("Bend between neighbouring triangles, in degrees, that gets the full Feature Spacing. Smaller bends get proportionally less.")]
        [Range(1f, 180f)] public float featureAngle = 45f;
        [Tooltip("Vertices closer than this (metres) count as one when finding neighbouring triangles across UV and normal seams.")]
        [Min(0f)] public float weldDistance = 0.0001f;
        public int seed = 1;

        public List<SurfaceSample> Sample(IReadOnlyList<SurfaceMesh> meshes)
        {
            var result = new List<SurfaceSample>();
            float scaleAtFeature = Mathf.Clamp(featureSpacing / spacing, 1e-3f, 1f);
            for (int m = 0; m < meshes.Count; m++)
            {
                float[] strength = FeatureStrength(meshes[m]);
                var radiusScale = new float[strength.Length];
                for (int t = 0; t < strength.Length; t++)
                    radiusScale[t] = math.lerp(1f, scaleAtFeature, strength[t]);
                PoissonDiskJob.Sample(meshes[m], m, spacing, radiusScale, seed, result);
            }
            return result;
        }

        // Per triangle in 0..1
        private float[] FeatureStrength(SurfaceMesh mesh)
        {
            int3[] tris = mesh.triangles;
            var faceNormals = new float3[tris.Length];
            for (int t = 0; t < tris.Length; t++)
            {
                float3 a = mesh.positions[tris[t].x];
                faceNormals[t] = math.normalizesafe(math.cross(mesh.positions[tris[t].y] - a, mesh.positions[tris[t].z] - a));
            }

            int[] weld = WeldVertices(mesh.positions);
            // Each undirected edge: the first triangle on it, then its bend
            var edgeTriangle = new Dictionary<int2, int>(tris.Length * 2);
            var strength = new float[tris.Length];
            var edgeSharp = new Dictionary<int2, float>(tris.Length * 2);
            float invAngle = 1f / featureAngle;
            for (int t = 0; t < tris.Length; t++)
            {
                for (int e = 0; e < 3; e++)
                {
                    int2 key = EdgeKey(weld[tris[t][e]], weld[tris[t][(e + 1) % 3]]);
                    if (edgeTriangle.TryGetValue(key, out int other))
                    {
                        float angle = math.degrees(math.acos(math.clamp(math.dot(faceNormals[t], faceNormals[other]), -1f, 1f)));
                        float sharp = math.saturate(angle * invAngle);
                        edgeSharp[key] = edgeSharp.TryGetValue(key, out float prev) ? math.max(prev, sharp) : sharp;
                    }
                    else
                    {
                        edgeTriangle[key] = t;
                    }
                }
            }
            for (int t = 0; t < tris.Length; t++)
            {
                float s = 0f;
                for (int e = 0; e < 3; e++)
                {
                    int2 key = EdgeKey(weld[tris[t][e]], weld[tris[t][(e + 1) % 3]]);
                    // Only one triangle on it: an open border
                    s = math.max(s, edgeSharp.TryGetValue(key, out float sharp) ? sharp : 1f);
                }
                strength[t] = s;
            }
            return strength;
        }

        // Index of the first vertex at (about) the same position, per vertex
        private int[] WeldVertices(float3[] positions)
        {
            var weld = new int[positions.Length];
            if (weldDistance <= 0f)
            {
                for (int v = 0; v < weld.Length; v++)
                    weld[v] = v;
                return weld;
            }
            float inv = 1f / weldDistance;
            var firstAt = new Dictionary<int3, int>(positions.Length);
            for (int v = 0; v < positions.Length; v++)
            {
                var cell = (int3)math.round(positions[v] * inv);
                if (!firstAt.TryGetValue(cell, out int first))
                    firstAt[cell] = first = v;
                weld[v] = first;
            }
            return weld;
        }

        // int2 hashes both halves (a long's hash would be a ^ b, which
        // collides constantly for neighbouring vertex indices)
        private static int2 EdgeKey(int a, int b) => a < b ? new int2(a, b) : new int2(b, a);
    }
}
