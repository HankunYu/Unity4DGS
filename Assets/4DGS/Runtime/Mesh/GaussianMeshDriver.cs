using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Moves the splats of a mesh-sampled asset (.meshsplat) with the meshes
    // they were sampled from: skinned, animated or simply moved, the splats
    // stay on the same surface spots. The binding carries each mesh's
    // vertices and bone weights, so the driver skins on the GPU from the bone
    // transforms alone: the source meshes need not render (hide them, or
    // disable them) and nothing waits for Unity's own skinning. A mesh the
    // binding marks as vertex-animated (a vertex cache such as Alembic) is
    // read from its own vertex buffer instead, as whatever plays it left it.
    // The result is a base pose that sorting, culling, animation modifiers
    // and the view data all use; GaussianMorph, when active, replaces it.
    // Blend shapes are not followed.
    //
    // Runs in LateUpdate ahead of GaussianAnimator, once animation and
    // constraints have posed the bones, and on every render in edit mode,
    // where LateUpdate does not tick (e.g. while scrubbing a Timeline).
    [AddComponentMenu("Gaussian Splatting/Mesh Driver")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianMeshDriver : MonoBehaviour
    {
        private static readonly int PropEntries = Shader.PropertyToID("_DriveEntries");
        private static readonly int PropVertices = Shader.PropertyToID("_DriveVertices");
        private static readonly int PropBoneMatrices = Shader.PropertyToID("_DriveBoneMatrices");
        private static readonly int PropFirstEntry = Shader.PropertyToID("_DriveFirstEntry");
        private static readonly int PropEntryCount = Shader.PropertyToID("_DriveEntryCount");
        private static readonly int PropFirstVertex = Shader.PropertyToID("_DriveFirstVertex");
        private static readonly int PropFirstBone = Shader.PropertyToID("_DriveFirstBone");
        private static readonly int PropHandedness = Shader.PropertyToID("_DriveHandedness");
        private static readonly int PropMeshVertices = Shader.PropertyToID("_DriveMeshVertices");
        private static readonly int PropMeshStride = Shader.PropertyToID("_DriveMeshStride");
        private static readonly int PropMeshPositionOffset = Shader.PropertyToID("_DriveMeshPositionOffset");
        private static readonly int PropMeshNormalOffset = Shader.PropertyToID("_DriveMeshNormalOffset");
        private static readonly int PropDeformedBase = Shader.PropertyToID("_DeformedBase");
        private static readonly int PropSplatCount = Shader.PropertyToID("_DriveSplatCount");
        private static readonly int PropChunkBounds = Shader.PropertyToID("_DeformedChunkBounds");
        private static readonly int PropChunkCount = Shader.PropertyToID("_DriveChunkCount");
        private static readonly int PropMeshRanges = Shader.PropertyToID("_DriveMeshRanges");
        private static readonly int PropMeshCenters = Shader.PropertyToID("_DriveMeshCenters");
        private static readonly int PropMeshCount = Shader.PropertyToID("_DriveMeshCount");

        private const int ChunkSize = 256;
        private const int GroupSize = 256;
        private const int MeshCenterGroupSize = 64;
        // The splats' mesh indices take 16 bits each
        private const int MaxIndexedMeshes = 1 << 16;

        [Tooltip("Binding imported with the splat asset (the .meshsplat file's Binding sub-asset).")]
        public GaussianMeshBinding binding;
        [Tooltip("Root of the model instance whose meshes drive the splats.")]
        public Transform source;
        [Tooltip("Stop drawing the source meshes while they drive the splats.")]
        public bool hideSourceMeshes = true;

        private GaussianSplatRenderer _renderer;
        private ComputeShader _shader;
        private int _kernelDrive = -1;
        private int _kernelDriveFromMesh = -1;
        private int _kernelBounds = -1;
        private int _kernelMeshCenters = -1;

        private GraphicsBuffer _entries;
        private GraphicsBuffer _vertices;
        private GraphicsBuffer _boneMatrices;
        private GraphicsBuffer _deformedBase;
        private GraphicsBuffer _chunkBounds;
        // The binding's baked occlusion, null when it has none
        private GraphicsBuffer _occlusion;
        // Which mesh each splat sits on (16 bits per splat, two to a uint),
        // each mesh's entry range, and each mesh's centre as last driven:
        // for what treats the meshes apart, e.g. a style's depth mask. Null
        // when the binding has more meshes than the indices can name.
        private GraphicsBuffer _splatMeshes;
        private GraphicsBuffer _meshRanges;
        private GraphicsBuffer _meshCenters;
        private GaussianMeshBinding _buffersFor;
        private Matrix4x4[] _boneMatrixData;

        // Per binding mesh, found by transform path below source: the
        // renderer, the transforms its bind poses refer to (skinned meshes)
        // and the filter holding its current vertices (vertex-animated ones)
        private Renderer[] _meshRenderers;
        private Transform[][] _meshBones;
        private MeshFilter[] _meshFilters;
        // Vertex-animated meshes whose vertices could not be read, each
        // warned about once
        private bool[] _meshUnreadable;
        private GaussianMeshBinding _resolvedBinding;
        private Transform _resolvedSource;
        private readonly List<Renderer> _hidden = new();

        public bool IsDriving { get; private set; }

        private void OnEnable()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            _shader = Resources.Load<ComputeShader>("GaussianMeshDrive");
            if (_shader != null)
            {
                _kernelDrive = _shader.FindKernel("CSDriveSplats");
                _kernelDriveFromMesh = _shader.FindKernel("CSDriveSplatsFromMesh");
                _kernelBounds = _shader.FindKernel("CSDriveChunkBounds");
                _kernelMeshCenters = _shader.FindKernel("CSDriveMeshCenters");
            }
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            ShowSourceMeshes();
            ReleaseBuffers();
            _meshRenderers = null;
            _meshBones = null;
            _meshFilters = null;
            _meshUnreadable = null;
            _resolvedBinding = null;
            _resolvedSource = null;
        }

        private void LateUpdate()
        {
            if (Application.isPlaying)
                Drive();
        }

        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (!Application.isPlaying)
                Drive();
        }

        // Re-evaluates the splats on the current bone transforms and animated
        // vertices. Runs on its own (see above); call it after moving bones or
        // stepping a vertex cache at another point.
        public void Drive()
        {
            if (!CanDrive() || !ResolveSource())
            {
                Stop();
                return;
            }
            UpdateSourceMeshVisibility();
            EnsureBuffers();

            Matrix4x4 worldToObject = _renderer.transform.worldToLocalMatrix;
            for (int m = 0; m < binding.meshes.Length; m++)
            {
                GaussianMeshBinding.MeshInfo info = binding.meshes[m];
                for (int b = 0; b < info.boneCount; b++)
                {
                    Transform bone = info.motion == GaussianMeshMotion.Skinned ? _meshBones[m][b] : _meshRenderers[m].transform;
                    _boneMatrixData[info.firstBone + b] = worldToObject * bone.localToWorldMatrix * binding.bindposes[info.firstBone + b];
                }
            }
            _boneMatrices.SetData(_boneMatrixData);

            _shader.SetBuffer(_kernelDrive, PropEntries, _entries);
            _shader.SetBuffer(_kernelDrive, PropVertices, _vertices);
            _shader.SetBuffer(_kernelDrive, PropBoneMatrices, _boneMatrices);
            _shader.SetBuffer(_kernelDrive, PropDeformedBase, _deformedBase);
            _shader.SetBuffer(_kernelDriveFromMesh, PropEntries, _entries);
            _shader.SetBuffer(_kernelDriveFromMesh, PropBoneMatrices, _boneMatrices);
            _shader.SetBuffer(_kernelDriveFromMesh, PropDeformedBase, _deformedBase);
            _shader.SetFloat(PropHandedness, worldToObject.determinant < 0 ? -1f : 1f);
            for (int m = 0; m < binding.meshes.Length; m++)
            {
                GaussianMeshBinding.MeshInfo info = binding.meshes[m];
                if (info.entryCount == 0)
                    continue;
                _shader.SetInt(PropFirstEntry, info.firstEntry);
                _shader.SetInt(PropEntryCount, info.entryCount);
                _shader.SetInt(PropFirstVertex, info.firstVertex);
                _shader.SetInt(PropFirstBone, info.firstBone);
                int groups = (info.entryCount + GroupSize - 1) / GroupSize;
                // Without readable vertices the mesh keeps its sampled shape
                // and still follows its transform
                GraphicsBuffer meshVertices = info.motion == GaussianMeshMotion.Vertices ? BindMeshVertices(m) : null;
                if (meshVertices != null)
                {
                    _shader.Dispatch(_kernelDriveFromMesh, groups, 1, 1);
                    meshVertices.Dispose();
                }
                else
                {
                    _shader.Dispatch(_kernelDrive, groups, 1, 1);
                }
            }

            int splatCount = binding.splatCount;
            int chunkCount = (splatCount + ChunkSize - 1) / ChunkSize;
            _shader.SetBuffer(_kernelBounds, PropDeformedBase, _deformedBase);
            _shader.SetBuffer(_kernelBounds, PropChunkBounds, _chunkBounds);
            _shader.SetInt(PropSplatCount, splatCount);
            _shader.SetInt(PropChunkCount, chunkCount);
            _shader.Dispatch(_kernelBounds, chunkCount, 1, 1);

            if (_meshCenters != null)
            {
                _shader.SetBuffer(_kernelMeshCenters, PropEntries, _entries);
                _shader.SetBuffer(_kernelMeshCenters, PropDeformedBase, _deformedBase);
                _shader.SetBuffer(_kernelMeshCenters, PropMeshRanges, _meshRanges);
                _shader.SetBuffer(_kernelMeshCenters, PropMeshCenters, _meshCenters);
                _shader.SetInt(PropMeshCount, binding.meshes.Length);
                _shader.Dispatch(_kernelMeshCenters, (binding.meshes.Length + MeshCenterGroupSize - 1) / MeshCenterGroupSize, 1, 1);
            }

            _renderer.SetDeformedBase(_deformedBase, _chunkBounds);
            _renderer.SetOcclusion(_occlusion);
            _renderer.SetSplatMeshes(_splatMeshes, _meshCenters);
            IsDriving = true;
        }

        // Points the from-mesh kernel at the mesh's current vertex buffer;
        // null when it cannot be read that way. The caller disposes the
        // returned handle once it has dispatched.
        private GraphicsBuffer BindMeshVertices(int m)
        {
            Mesh mesh = _meshFilters[m].sharedMesh;
            int stream = mesh != null ? mesh.GetVertexAttributeStream(VertexAttribute.Position) : -1;
            if (stream < 0)
                return MeshUnreadable(m, "it has no mesh");
            if (mesh.vertexCount != binding.meshes[m].vertexCount)
                return MeshUnreadable(m, $"it has {mesh.vertexCount} vertices; the binding has {binding.meshes[m].vertexCount}");
            if (!IsFloat3(mesh, VertexAttribute.Position))
                return MeshUnreadable(m, "its positions are not three floats");

            // A compute shader reads the buffer only as a raw one, which the
            // mesh has to ask for before it uploads its vertices
            if ((mesh.vertexBufferTarget & GraphicsBuffer.Target.Raw) == 0)
            {
                mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                mesh.UploadMeshData(false);
            }
            GraphicsBuffer buffer = mesh.GetVertexBuffer(stream);
            if (buffer == null)
                return null;
            if ((buffer.target & GraphicsBuffer.Target.Raw) == 0)
            {
                // Still the buffer from before: raw from the next upload on
                buffer.Dispose();
                return null;
            }

            bool hasNormal = mesh.GetVertexAttributeStream(VertexAttribute.Normal) == stream && IsFloat3(mesh, VertexAttribute.Normal);
            _shader.SetBuffer(_kernelDriveFromMesh, PropMeshVertices, buffer);
            _shader.SetInt(PropMeshStride, buffer.stride);
            _shader.SetInt(PropMeshPositionOffset, mesh.GetVertexAttributeOffset(VertexAttribute.Position));
            _shader.SetInt(PropMeshNormalOffset, hasNormal ? mesh.GetVertexAttributeOffset(VertexAttribute.Normal) : -1);
            return buffer;
        }

        private static bool IsFloat3(Mesh mesh, VertexAttribute attribute) =>
            mesh.GetVertexAttributeFormat(attribute) == VertexAttributeFormat.Float32 &&
            mesh.GetVertexAttributeDimension(attribute) == 3;

        private GraphicsBuffer MeshUnreadable(int m, string message)
        {
            if (!_meshUnreadable[m])
                Debug.LogWarning($"{name}: cannot follow the vertices of {binding.meshes[m].path}: {message}", this);
            _meshUnreadable[m] = true;
            return null;
        }

        private bool CanDrive()
        {
            if (_shader == null || _kernelDrive < 0 || _kernelDriveFromMesh < 0 || binding == null || source == null)
                return false;
            if (binding.entryData == null || binding.vertexData == null || binding.meshes == null || binding.bindposes == null)
                return false;
            if (_renderer == null || !_renderer.HasValidAsset || !_renderer.HasValidRenderSetup)
                return false;
            return binding.Matches(_renderer.asset) && binding.EntryCount == binding.splatCount;
        }

        private void Stop()
        {
            if (IsDriving && _renderer != null)
            {
                _renderer.ClearDeformedBase();
                _renderer.SetOcclusion(null);
                _renderer.SetSplatMeshes(null, null);
            }
            IsDriving = false;
        }

        // Finds each binding mesh's renderer and bones below source; false
        // (with a warning, once per source) when the model does not match
        private bool ResolveSource()
        {
            if (_resolvedBinding == binding && _resolvedSource == source)
                return _meshRenderers != null;
            ShowSourceMeshes();
            _resolvedBinding = binding;
            _resolvedSource = source;
            _meshRenderers = null;
            _meshBones = null;
            _meshFilters = null;
            _meshUnreadable = null;

            var renderers = new Renderer[binding.meshes.Length];
            var bones = new Transform[binding.meshes.Length][];
            var filters = new MeshFilter[binding.meshes.Length];
            for (int m = 0; m < binding.meshes.Length; m++)
            {
                GaussianMeshBinding.MeshInfo info = binding.meshes[m];
                Transform t = string.IsNullOrEmpty(info.path) ? source : source.Find(info.path);
                renderers[m] = t != null ? t.GetComponent<Renderer>() : null;
                if (renderers[m] == null)
                    return Mismatch($"no renderer at {info.path}");
                if (info.motion == GaussianMeshMotion.Vertices && !t.TryGetComponent(out filters[m]))
                    return Mismatch($"{info.path} has no mesh filter");
                if (info.motion != GaussianMeshMotion.Skinned)
                    continue;
                if (renderers[m] is not SkinnedMeshRenderer skinned)
                    return Mismatch($"{info.path} is not a skinned mesh");
                bones[m] = skinned.bones;
                if (bones[m].Length != info.boneCount || System.Array.IndexOf(bones[m], null) >= 0)
                    return Mismatch($"{info.path} has {bones[m].Length} bones or some are missing; the binding has {info.boneCount}");
            }
            _meshRenderers = renderers;
            _meshBones = bones;
            _meshFilters = filters;
            _meshUnreadable = new bool[binding.meshes.Length];
            return true;
        }

        private bool Mismatch(string message)
        {
            Debug.LogWarning($"{name}: {source.name} does not match {binding.name}: {message}", this);
            return false;
        }

        private void UpdateSourceMeshVisibility()
        {
            if (!hideSourceMeshes)
            {
                ShowSourceMeshes();
                return;
            }
            if (_hidden.Count > 0)
                return;
            // forceRenderingOff is not saved with the scene, so hiding in edit
            // mode leaves the model untouched
            foreach (Renderer r in _meshRenderers)
            {
                if (r == null || r.forceRenderingOff)
                    continue;
                r.forceRenderingOff = true;
                _hidden.Add(r);
            }
        }

        private void ShowSourceMeshes()
        {
            foreach (Renderer r in _hidden)
            {
                if (r != null)
                    r.forceRenderingOff = false;
            }
            _hidden.Clear();
        }

        private void EnsureBuffers()
        {
            if (_buffersFor == binding && _entries != null)
                return;
            ReleaseBuffers();
            int splatCount = binding.splatCount;
            int chunkCount = (splatCount + ChunkSize - 1) / ChunkSize;
            _entries = new GraphicsBuffer(GraphicsBuffer.Target.Structured, binding.EntryCount, GaussianMeshBinding.EntrySize)
                { name = "GaussianMeshBindingEntries" };
            _entries.SetData(binding.GetEntries());
            _vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, binding.VertexCount, GaussianMeshBinding.VertexSize)
                { name = "GaussianMeshBindingVertices" };
            _vertices.SetData(binding.GetVertices());
            _boneMatrices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, binding.bindposes.Length, 64)
                { name = "GaussianMeshBoneMatrices" };
            _boneMatrixData = new Matrix4x4[binding.bindposes.Length];
            _deformedBase = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount * 2, 16)
                { name = "GaussianMeshDeformedBase" };
            _chunkBounds = new GraphicsBuffer(GraphicsBuffer.Target.Structured, chunkCount * 2, 16)
                { name = "GaussianMeshDeformedChunkBounds" };
            if (binding.HasOcclusion)
            {
                NativeArray<uint> occlusion = binding.GetOcclusion();
                _occlusion = new GraphicsBuffer(GraphicsBuffer.Target.Structured, occlusion.Length, 4)
                    { name = "GaussianMeshOcclusion" };
                _occlusion.SetData(occlusion);
            }
            CreateMeshIndexBuffers();
            _buffersFor = binding;
        }

        private void CreateMeshIndexBuffers()
        {
            int meshCount = binding.meshes.Length;
            if (meshCount == 0 || meshCount > MaxIndexedMeshes)
                return;
            NativeArray<GaussianMeshBinding.Entry> entries = binding.GetEntries();
            var splatMeshes = new NativeArray<uint>((binding.splatCount + 1) / 2, Allocator.Temp);
            var ranges = new NativeArray<Vector2Int>(meshCount, Allocator.Temp);
            for (int m = 0; m < meshCount; m++)
            {
                GaussianMeshBinding.MeshInfo info = binding.meshes[m];
                ranges[m] = new Vector2Int(info.firstEntry, info.entryCount);
                for (int e = info.firstEntry; e < info.firstEntry + info.entryCount; e++)
                {
                    int splat = (int)entries[e].splat;
                    splatMeshes[splat >> 1] |= (uint)m << ((splat & 1) * 16);
                }
            }
            _splatMeshes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatMeshes.Length, 4)
                { name = "GaussianMeshSplatMeshes" };
            _splatMeshes.SetData(splatMeshes);
            _meshRanges = new GraphicsBuffer(GraphicsBuffer.Target.Structured, meshCount, 8)
                { name = "GaussianMeshRanges" };
            _meshRanges.SetData(ranges);
            _meshCenters = new GraphicsBuffer(GraphicsBuffer.Target.Structured, meshCount, 16)
                { name = "GaussianMeshCenters" };
            splatMeshes.Dispose();
            ranges.Dispose();
        }

        private void ReleaseBuffers()
        {
            Stop();
            _entries?.Dispose();
            _entries = null;
            _vertices?.Dispose();
            _vertices = null;
            _boneMatrices?.Dispose();
            _boneMatrices = null;
            _deformedBase?.Dispose();
            _deformedBase = null;
            _occlusion?.Dispose();
            _occlusion = null;
            _splatMeshes?.Dispose();
            _splatMeshes = null;
            _meshRanges?.Dispose();
            _meshRanges = null;
            _meshCenters?.Dispose();
            _meshCenters = null;
            _chunkBounds?.Dispose();
            _chunkBounds = null;
            _buffersFor = null;
        }
    }
}
