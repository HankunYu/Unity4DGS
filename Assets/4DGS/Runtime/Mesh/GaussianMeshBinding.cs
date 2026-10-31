using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Ties every splat of a mesh-sampled asset to a spot on one of the
    // model's meshes, with what it takes to skin those meshes, so
    // GaussianMeshDriver can move the splats from the bone transforms alone.
    // Imported next to the asset it belongs to.
    public class GaussianMeshBinding : ScriptableObject
    {
        [Serializable]
        public struct MeshInfo
        {
            // Transform path of the renderer below the model root
            public string path;
            // Skinned meshes follow the renderer's bones, others its transform
            public bool skinned;
            // This mesh's entries are firstEntry .. firstEntry + entryCount - 1
            public int firstEntry;
            public int entryCount;
            public int firstVertex;
            public int vertexCount;
            // Its bind poses are bindposes[firstBone .. firstBone + boneCount - 1]
            public int firstBone;
            public int boneCount;
        }

        // Layout must match BindingEntry in GaussianMeshDrive.compute
        public struct Entry
        {
            public uint splat;
            // Mesh-local vertex indices
            public uint3 vertices;
            // Weights of vertices.x and vertices.y; vertices.z gets the rest
            public float2 bary;
        }

        // Layout must match BindingVertex in GaussianMeshDrive.compute
        public struct Vertex
        {
            // Mesh space, bind pose
            public float3 position;
            public float3 normal;
            // Four strongest bones (mesh-local indices) and their weights,
            // summing to 1; a rigid mesh has its one "bone" at weight 1
            public uint4 bones;
            public float4 weights;
        }

        public const int EntrySize = 24;
        public const int VertexSize = 56;

        // The model the splats were sampled from, for setting up a scene
        public GameObject model;
        public MeshInfo[] meshes;
        public Matrix4x4[] bindposes;
        // Entries grouped by mesh, EntrySize bytes each
        public TextAsset entryData;
        // Vertices of all meshes, VertexSize bytes each
        public TextAsset vertexData;
        // Hash of the splat asset these entries index into
        public Hash128 assetDataHash;
        public int splatCount;

        public int EntryCount => entryData != null ? (int)(entryData.dataSize / EntrySize) : 0;
        public int VertexCount => vertexData != null ? (int)(vertexData.dataSize / VertexSize) : 0;

        public NativeArray<Entry> GetEntries() => entryData.GetData<Entry>();
        public NativeArray<Vertex> GetVertices() => vertexData.GetData<Vertex>();

        public bool Matches(GaussianSplatAsset asset) =>
            asset != null && asset.splatCount == splatCount && asset.dataHash == assetDataHash;
    }
}
