using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Generates a batched mesh for VR stereo-compatible splat rendering.
    /// Each mesh instance contains <see cref="SplatsPerBatch"/> quads.
    /// Vertex position.z encodes the local splat index within the batch
    /// (reinterpreted as uint in shader via asuint).
    /// </summary>
    public static class GaussianSplatVRMesh
    {
        public const int SplatsPerBatch = 128;

        private static Mesh _cachedMesh;

        public static Mesh GetOrCreate()
        {
            if (_cachedMesh != null)
                return _cachedMesh;

            _cachedMesh = CreateMesh(SplatsPerBatch);
            return _cachedMesh;
        }

        private static Mesh CreateMesh(int splatsPerBatch)
        {
            var positions = new Vector3[splatsPerBatch * 4];
            var indices = new int[splatsPerBatch * 6];

            for (int i = 0; i < splatsPerBatch; i++)
            {
                // Encode local splat index in z component (reinterpreted as uint in shader)
                uint ui = (uint)i;
                float z;
                unsafe { z = *(float*)&ui; }

                int v = i * 4;
                positions[v + 0] = new Vector3(-1, -1, z);
                positions[v + 1] = new Vector3( 1, -1, z);
                positions[v + 2] = new Vector3(-1,  1, z);
                positions[v + 3] = new Vector3( 1,  1, z);

                int t = i * 6;
                indices[t + 0] = v + 0;
                indices[t + 1] = v + 1;
                indices[t + 2] = v + 2;
                indices[t + 3] = v + 1;
                indices[t + 4] = v + 3;
                indices[t + 5] = v + 2;
            }

            var mesh = new Mesh
            {
                name = "GaussianSplatVRBatch",
                vertices = positions,
                triangles = indices,
                bounds = new Bounds(Vector3.zero, Vector3.one * 10000f),
                hideFlags = HideFlags.HideAndDontSave
            };
            return mesh;
        }

        public static void Dispose()
        {
            if (_cachedMesh != null)
            {
                UnityEngine.Object.DestroyImmediate(_cachedMesh);
                _cachedMesh = null;
            }
        }

        public static int CalcInstanceCount(int splatCount)
        {
            return Mathf.CeilToInt(splatCount / (float)SplatsPerBatch);
        }
    }
}
