using System;
using System.Collections.Generic;
using System.Linq;
using GaussianSplatting.Editor.Import;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Model -> surface samples -> linear-space splats, ready for
    // GaussianSplatAssetBuilder. Splats come out in the PLY convention
    // (x right, y down, z forward) like every other splat source, so the
    // usual (1,-1,1) renderer scale puts them back onto the model. Each
    // splat's local +Z is the outward surface normal, which backface culling
    // relies on.
    public static class MeshSplatBaker
    {
        // Every mesh drawn by a MeshRenderer or SkinnedMeshRenderer under the
        // model, inactive ones included. Skinned meshes are taken in their
        // bind pose, which is where the renderer would draw them with the
        // bones at rest.
        public static List<SurfaceMesh> CollectMeshes(GameObject model)
        {
            var meshes = new List<SurfaceMesh>();
            Matrix4x4 toModel = model.transform.worldToLocalMatrix;
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = renderer switch
                {
                    SkinnedMeshRenderer skinned => skinned.sharedMesh,
                    MeshRenderer => renderer.TryGetComponent(out MeshFilter filter) ? filter.sharedMesh : null,
                    _ => null,
                };
                if (mesh == null)
                    continue;
                SurfaceMesh surface = ToSurfaceMesh(mesh, toModel * renderer.transform.localToWorldMatrix,
                    TransformPath(model.transform, renderer.transform));
                surface.source = mesh;
                surface.materials = renderer.sharedMaterials;
                surface.skinned = renderer is SkinnedMeshRenderer && mesh.bindposeCount > 0;
                meshes.Add(surface);
            }
            return meshes;
        }

        public static NativeArray<InputSplatData> BuildSplats(IReadOnlyList<SurfaceMesh> meshes,
            IReadOnlyList<SurfaceSample> samples, MeshSplatSettings settings)
        {
            if (samples.Count == 0)
                throw new InvalidOperationException("the sampler placed no splats");

            float3[] colors = FillColors(meshes, samples, settings);

            float3 light = math.normalizesafe((float3)settings.lightDirection, new float3(0, 1, 0));

            var splats = new NativeArray<InputSplatData>(samples.Count, Allocator.Persistent);
            for (int i = 0; i < samples.Count; i++)
            {
                SurfaceSample sample = samples[i];
                SurfaceMesh mesh = meshes[sample.mesh];
                float3 normal = mesh.Normal(sample);
                float3 color = colors[i];
                if (settings.bakeLighting)
                    color *= settings.ambient + (1f - settings.ambient) * math.max(0f, math.dot(normal, light));

                float sigma = settings.splatSize * sample.spacing;
                quaternion rot = SurfaceFrame(mesh, sample.triangle, normal);
                splats[i] = new InputSplatData
                {
                    pos = ToSource(mesh.Position(sample)),
                    nor = ToSource(normal),
                    dc0 = math.saturate(color),
                    opacity = settings.opacity,
                    scale = new Vector3(sigma, sigma, sigma * settings.splatThickness),
                    rot = ToSourcePacked(rot),
                };
            }
            return splats;
        }

        // Each color source fills the samples whose material uses it
        private static float3[] FillColors(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples,
            MeshSplatSettings settings)
        {
            var kinds = new SplatColorKind[samples.Count];
            for (int i = 0; i < samples.Count; i++)
                kinds[i] = settings.ColorKindFor(meshes[samples[i].mesh].MaterialAt(samples[i]));

            var colors = new float3[samples.Count];
            foreach (SplatColorKind kind in kinds.Distinct())
            {
                int[] indices = Enumerable.Range(0, samples.Count).Where(i => kinds[i] == kind).ToArray();
                var subset = indices.Select(i => samples[i]).ToArray();
                var subsetColors = new float3[indices.Length];
                settings.ColorSource(kind).Fill(meshes, subset, subsetColors);
                for (int i = 0; i < indices.Length; i++)
                    colors[indices[i]] = subsetColors[i];
            }
            return colors;
        }

        // One binding entry per asset splat, grouped by mesh, plus each
        // mesh's vertices with their four strongest bones. sourceIndices maps
        // each asset splat back to its sample (see GaussianSplatBuildResult).
        public static void BuildBinding(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples,
            NativeArray<int> sourceIndices, out GaussianMeshBinding.MeshInfo[] meshInfos,
            out NativeArray<GaussianMeshBinding.Entry> entries, out NativeArray<GaussianMeshBinding.Vertex> vertices,
            out Matrix4x4[] bindposes)
        {
            meshInfos = new GaussianMeshBinding.MeshInfo[meshes.Count];
            for (int splat = 0; splat < sourceIndices.Length; splat++)
                meshInfos[samples[sourceIndices[splat]].mesh].entryCount++;
            var allBindposes = new List<Matrix4x4>();
            int firstEntry = 0, firstVertex = 0;
            for (int m = 0; m < meshes.Count; m++)
            {
                SurfaceMesh mesh = meshes[m];
                ref GaussianMeshBinding.MeshInfo info = ref meshInfos[m];
                info.path = mesh.path;
                info.skinned = mesh.skinned;
                info.firstEntry = firstEntry;
                info.firstVertex = firstVertex;
                info.vertexCount = mesh.positions.Length;
                info.firstBone = allBindposes.Count;
                if (mesh.skinned)
                    allBindposes.AddRange(mesh.source.bindposes);
                else
                    allBindposes.Add(Matrix4x4.identity);
                info.boneCount = allBindposes.Count - info.firstBone;
                firstEntry += info.entryCount;
                firstVertex += info.vertexCount;
            }
            bindposes = allBindposes.ToArray();

            entries = new NativeArray<GaussianMeshBinding.Entry>(sourceIndices.Length, Allocator.Persistent);
            var next = new int[meshes.Count];
            for (int m = 0; m < meshes.Count; m++)
                next[m] = meshInfos[m].firstEntry;
            for (int splat = 0; splat < sourceIndices.Length; splat++)
            {
                SurfaceSample sample = samples[sourceIndices[splat]];
                int3 tri = meshes[sample.mesh].triangles[sample.triangle];
                entries[next[sample.mesh]++] = new GaussianMeshBinding.Entry
                {
                    splat = (uint)splat,
                    vertices = (uint3)tri,
                    bary = sample.bary.xy,
                };
            }

            vertices = new NativeArray<GaussianMeshBinding.Vertex>(firstVertex, Allocator.Persistent);
            for (int m = 0; m < meshes.Count; m++)
                vertices.GetSubArray(meshInfos[m].firstVertex, meshInfos[m].vertexCount).CopyFrom(SkinVertices(meshes[m]));
        }

        // The mesh's vertices for skinning: unlike SurfaceMesh, in the mesh's
        // own space, with its four strongest bones
        public static GaussianMeshBinding.Vertex[] SkinVertices(SurfaceMesh mesh)
        {
            Vector3[] positions = mesh.source.vertices;
            Vector3[] normals = mesh.source.normals;
            bool hasNormals = normals.Length == positions.Length;
            NativeArray<byte> bonesPerVertex = mesh.skinned ? mesh.source.GetBonesPerVertex() : default;
            NativeArray<BoneWeight1> weights = mesh.skinned ? mesh.source.GetAllBoneWeights() : default;
            var vertices = new GaussianMeshBinding.Vertex[positions.Length];
            int w = 0;
            for (int v = 0; v < positions.Length; v++)
            {
                var vertex = new GaussianMeshBinding.Vertex
                {
                    position = positions[v],
                    normal = hasNormals ? (float3)normals[v] : float3.zero,
                    weights = new float4(1, 0, 0, 0),
                };
                if (mesh.skinned)
                {
                    // Unity sorts each vertex's weights strongest first
                    int count = bonesPerVertex[v];
                    float sum = 0;
                    for (int k = 0; k < count; k++, w++)
                    {
                        if (k >= 4)
                            continue;
                        vertex.bones[k] = (uint)weights[w].boneIndex;
                        vertex.weights[k] = weights[w].weight;
                        sum += weights[w].weight;
                    }
                    if (count == 0)
                        vertex.weights = float4.zero;
                    else
                        vertex.weights /= sum;
                }
                vertices[v] = vertex;
            }
            return vertices;
        }

        private static SurfaceMesh ToSurfaceMesh(Mesh mesh, Matrix4x4 toModel, string path)
        {
            Vector3[] vertices = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Matrix4x4 normalMatrix = toModel.inverse.transpose;
            var uvs = new List<Vector2>();
            mesh.GetUVs(0, uvs);
            var surface = new SurfaceMesh
            {
                path = path,
                positions = new float3[vertices.Length],
                normals = new float3[vertices.Length],
                uvs = uvs.Count == vertices.Length ? uvs.Select(uv => (float2)uv).ToArray() : Array.Empty<float2>(),
                colors = mesh.colors.Length == vertices.Length ? mesh.colors : Array.Empty<Color>(),
            };
            for (int v = 0; v < vertices.Length; v++)
            {
                surface.positions[v] = toModel.MultiplyPoint3x4(vertices[v]);
                surface.normals[v] = normals.Length == vertices.Length
                    ? math.normalizesafe((float3)normalMatrix.MultiplyVector(normals[v]))
                    : float3.zero; // no normals: SurfaceMesh falls back to face normals
            }

            var triangles = new List<int3>();
            var triangleMaterials = new List<int>();
            for (int sub = 0; sub < mesh.subMeshCount; sub++)
            {
                if (mesh.GetTopology(sub) != MeshTopology.Triangles)
                    continue;
                int[] indices = mesh.GetTriangles(sub);
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    triangles.Add(new int3(indices[t], indices[t + 1], indices[t + 2]));
                    triangleMaterials.Add(sub);
                }
            }
            surface.triangles = triangles.ToArray();
            surface.triangleMaterials = triangleMaterials.ToArray();
            return surface;
        }

        // Local +Z on the normal, +X along the triangle's first edge: any
        // tangent works for round splats, but a stable one keeps stretched
        // splats (stylized streaks) from spinning on the surface
        private static quaternion SurfaceFrame(SurfaceMesh mesh, int triangle, float3 normal)
        {
            int3 t = mesh.triangles[triangle];
            float3 edge = mesh.positions[t.y] - mesh.positions[t.x];
            float3 tangent = edge;
            if (math.lengthsq(math.cross(edge, normal)) < 1e-12f)
                tangent = math.abs(normal.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0);
            tangent = math.normalize(tangent - normal * math.dot(tangent, normal));
            return quaternion.LookRotation(normal, math.cross(normal, tangent));
        }

        // Unity (y up) -> PLY convention (y down): a mirror, so rotations
        // flip their x and z components
        private static Vector3 ToSource(float3 v) => new(v.x, -v.y, v.z);

        private static Quaternion ToSourcePacked(quaternion q)
        {
            float4 mirrored = math.normalize(new float4(-q.value.x, q.value.y, -q.value.z, q.value.w));
            float4 packed = GaussianUtils.PackSmallest3Rotation(mirrored);
            return new Quaternion(packed.x, packed.y, packed.z, packed.w);
        }

        private static string TransformPath(Transform root, Transform t)
        {
            var parts = new List<string>();
            for (; t != null && t != root; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
