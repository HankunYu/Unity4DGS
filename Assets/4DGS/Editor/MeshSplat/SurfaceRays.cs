using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Rays leaving surface samples over the hemisphere around their normal,
    // for what judges a sample by the model around it (HiddenSurfaceFilter,
    // SurfaceOcclusion).
    internal static class SurfaceRays
    {
        // Samples are nudged this far off the surface, in metres, so their
        // rays do not hit the triangle they start on
        public const float Bias = 1e-4f;

        // Calls visit once per group of samples sharing the same cover: with
        // ownMeshOnly, each mesh's samples against that mesh alone, otherwise
        // every sample against all meshes. Groups without samples are skipped.
        public static void ForEachCover(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples,
            bool ownMeshOnly, Action<IReadOnlyList<SurfaceMesh>, List<int>> visit)
        {
            if (!ownMeshOnly)
            {
                var all = new List<int>(samples.Count);
                for (int i = 0; i < samples.Count; i++)
                    all.Add(i);
                if (all.Count > 0)
                    visit(meshes, all);
                return;
            }
            for (int m = 0; m < meshes.Count; m++)
            {
                var indices = new List<int>();
                for (int i = 0; i < samples.Count; i++)
                {
                    if (samples[i].mesh == m)
                        indices.Add(i);
                }
                if (indices.Count > 0)
                    visit(new[] { meshes[m] }, indices);
            }
        }

        // Ray origins and normals of the samples at indices
        public static void Fill(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples,
            List<int> indices, NativeArray<float3> origins, NativeArray<float3> normals)
        {
            for (int k = 0; k < indices.Count; k++)
            {
                SurfaceSample sample = samples[indices[k]];
                SurfaceMesh surface = meshes[sample.mesh];
                float3 normal = surface.Normal(sample);
                normals[k] = normal;
                origins[k] = surface.Position(sample) + normal * Bias;
            }
        }

        public static void Frame(float3 n, out float3 tangent, out float3 bitangent)
        {
            tangent = math.normalize(math.abs(n.y) < 0.99f ? math.cross(n, new float3(0, 1, 0)) : math.cross(n, new float3(1, 0, 0)));
            bitangent = math.cross(n, tangent);
        }

        // Seeded by the sample's index within its group, so a re-import gives
        // the same rays; turn is the angle the sample's whole set is turned
        // by, so neighbours do not share the same gaps
        public static Unity.Mathematics.Random Seed(int index, out float turn)
        {
            var random = new Unity.Mathematics.Random(math.hash(new int2(index, 0x5eed)) | 1u);
            turn = random.NextFloat() * 2f * math.PI;
            return random;
        }

        // Ray k of rayCount: stratified cosine-weighted directions around n
        public static float3 Direction(float3 n, float3 tangent, float3 bitangent, int k, int rayCount, float turn,
            ref Unity.Mathematics.Random random)
        {
            float u = (k + random.NextFloat()) / rayCount;
            float phi = turn + k * 2.39996323f;
            float r = math.sqrt(u);
            math.sincos(phi, out float s, out float c);
            return math.normalize(r * c * tangent + r * s * bitangent + math.sqrt(1f - u) * n);
        }
    }
}
