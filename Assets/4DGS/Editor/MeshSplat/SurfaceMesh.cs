using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // One mesh of a model, in the model root's space (Unity axes) and in the
    // pose it is sampled in: the bind pose for skinned meshes.
    public sealed class SurfaceMesh
    {
        // Transform path below the model root, so a sample can be traced
        // back to the renderer it came from
        public string path;
        // The mesh asset and whether it is skinned, for the runtime binding
        public Mesh source;
        public bool skinned;
        public float3[] positions;
        public float3[] normals;
        // Empty when the mesh has none
        public float2[] uvs;
        public Color[] colors;
        public int3[] triangles;
        // Per triangle, the index into materials (its submesh's material)
        public int[] triangleMaterials;
        public Material[] materials;

        // The same mesh in another pose: everything but the vertices shared
        public SurfaceMesh WithPose(float3[] posedPositions, float3[] posedNormals)
        {
            var posed = (SurfaceMesh)MemberwiseClone();
            posed.positions = posedPositions;
            posed.normals = posedNormals;
            return posed;
        }

        public float TriangleArea(int triangle)
        {
            int3 t = triangles[triangle];
            float3 a = positions[t.x];
            return 0.5f * math.length(math.cross(positions[t.y] - a, positions[t.z] - a));
        }

        public float Area()
        {
            double sum = 0;
            for (int i = 0; i < triangles.Length; i++)
                sum += TriangleArea(i);
            return (float)sum;
        }

        public float3 Position(in SurfaceSample sample)
        {
            int3 t = triangles[sample.triangle];
            return sample.bary.x * positions[t.x] + sample.bary.y * positions[t.y] + sample.bary.z * positions[t.z];
        }

        public float2 Uv(in SurfaceSample sample)
        {
            int3 t = triangles[sample.triangle];
            return sample.bary.x * uvs[t.x] + sample.bary.y * uvs[t.y] + sample.bary.z * uvs[t.z];
        }

        public Color VertexColor(in SurfaceSample sample)
        {
            int3 t = triangles[sample.triangle];
            return sample.bary.x * colors[t.x] + sample.bary.y * colors[t.y] + sample.bary.z * colors[t.z];
        }

        // Interpolated vertex normal, or the face normal where those cancel out
        public float3 Normal(in SurfaceSample sample)
        {
            int3 t = triangles[sample.triangle];
            float3 n = sample.bary.x * normals[t.x] + sample.bary.y * normals[t.y] + sample.bary.z * normals[t.z];
            float len = math.length(n);
            if (len > 1e-6f)
                return n / len;
            float3 a = positions[t.x];
            return math.normalizesafe(math.cross(positions[t.y] - a, positions[t.z] - a), new float3(0, 1, 0));
        }
    }
}
