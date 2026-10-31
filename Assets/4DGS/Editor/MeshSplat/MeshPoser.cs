using System;
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Poses a model's meshes on the CPU, in the model root's space, the way
    // GaussianMeshDriver does on the GPU (linear blend skinning with the four
    // strongest bones, rigid meshes by their transform), so baked frames
    // match what the driver would show. Blend shapes are not applied.
    public sealed class MeshPoser
    {
        private readonly GameObject _instance;
        private readonly IReadOnlyList<SurfaceMesh> _rest;
        private readonly Renderer[] _renderers;
        private readonly GaussianMeshBinding.Vertex[][] _vertices;

        // instance: a scene copy of the model the rest meshes came from
        public MeshPoser(GameObject instance, IReadOnlyList<SurfaceMesh> rest)
        {
            _instance = instance;
            _rest = rest;
            _renderers = new Renderer[rest.Count];
            _vertices = new GaussianMeshBinding.Vertex[rest.Count][];
            for (int m = 0; m < rest.Count; m++)
            {
                Transform t = string.IsNullOrEmpty(rest[m].path) ? instance.transform : instance.transform.Find(rest[m].path);
                _renderers[m] = t != null ? t.GetComponent<Renderer>() : null;
                if (_renderers[m] == null)
                    throw new InvalidOperationException($"no renderer at {rest[m].path}");
                _vertices[m] = MeshSplatBaker.SkinVertices(rest[m]);
            }
        }

        // The meshes in the instance's current pose
        public List<SurfaceMesh> Pose()
        {
            Matrix4x4 toModel = _instance.transform.worldToLocalMatrix;
            var posed = new List<SurfaceMesh>(_rest.Count);
            for (int m = 0; m < _rest.Count; m++)
            {
                SurfaceMesh mesh = _rest[m];
                Matrix4x4[] bones = BoneMatrices(mesh, _renderers[m], toModel);
                GaussianMeshBinding.Vertex[] vertices = _vertices[m];
                var positions = new float3[vertices.Length];
                var normals = new float3[vertices.Length];
                for (int v = 0; v < vertices.Length; v++)
                {
                    GaussianMeshBinding.Vertex vertex = vertices[v];
                    float3 p = 0, n = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        if (vertex.weights[k] <= 0f)
                            continue;
                        Matrix4x4 bone = bones[vertex.bones[k]];
                        p += vertex.weights[k] * (float3)bone.MultiplyPoint3x4(vertex.position);
                        n += vertex.weights[k] * (float3)bone.MultiplyVector(vertex.normal);
                    }
                    positions[v] = p;
                    // Left as blended, like the driver: SurfaceMesh.Normal
                    // normalizes after interpolating
                    normals[v] = n;
                }
                posed.Add(mesh.WithPose(positions, normals));
            }
            return posed;
        }

        // Bind pose -> model space per bone; a rigid mesh has one
        private static Matrix4x4[] BoneMatrices(SurfaceMesh mesh, Renderer renderer, Matrix4x4 toModel)
        {
            if (!mesh.skinned)
                return new[] { toModel * renderer.transform.localToWorldMatrix };
            Transform[] bones = ((SkinnedMeshRenderer)renderer).bones;
            Matrix4x4[] bindposes = mesh.source.bindposes;
            if (bones.Length != bindposes.Length)
                throw new InvalidOperationException($"{mesh.path} has {bones.Length} bones for {bindposes.Length} bind poses");
            var matrices = new Matrix4x4[bones.Length];
            for (int b = 0; b < bones.Length; b++)
                matrices[b] = bones[b] != null ? toModel * bones[b].localToWorldMatrix * bindposes[b] : Matrix4x4.zero;
            return matrices;
        }
    }
}
