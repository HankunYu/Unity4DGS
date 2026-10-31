using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Moves the splats of a mesh-sampled asset (.meshsplat) with the meshes
    // they were sampled from: skinned, animated or simply moved, the splats
    // stay on the same surface spots. The binding carries each mesh's
    // vertices and bone weights, so the driver skins on the GPU from the bone
    // transforms alone: the source meshes need not render (hide them, or
    // disable them) and nothing waits for Unity's own skinning. The result
    // is a base pose that sorting, culling, animation modifiers and the view
    // data all use; GaussianMorph, when active, replaces it. Blend shapes are
    // not followed.
    //
    // Runs in LateUpdate ahead of GaussianAnimator, once animation and
    // constraints have posed the bones, and on every render in edit mode,
    // where LateUpdate does not tick (e.g. while scrubbing a Timeline).
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
        private static readonly int PropDeformedBase = Shader.PropertyToID("_DeformedBase");
        private static readonly int PropSplatCount = Shader.PropertyToID("_DriveSplatCount");
        private static readonly int PropChunkBounds = Shader.PropertyToID("_DeformedChunkBounds");
        private static readonly int PropChunkCount = Shader.PropertyToID("_DriveChunkCount");

        private const int ChunkSize = 256;
        private const int GroupSize = 256;

        [Tooltip("Binding imported with the splat asset (the .meshsplat file's Binding sub-asset).")]
        public GaussianMeshBinding binding;
        [Tooltip("Root of the model instance whose meshes drive the splats.")]
        public Transform source;
        [Tooltip("Stop drawing the source meshes while they drive the splats.")]
        public bool hideSourceMeshes = true;

        private GaussianSplatRenderer _renderer;
        private ComputeShader _shader;
        private int _kernelDrive = -1;
        private int _kernelBounds = -1;

        private GraphicsBuffer _entries;
        private GraphicsBuffer _vertices;
        private GraphicsBuffer _boneMatrices;
        private GraphicsBuffer _deformedBase;
        private GraphicsBuffer _chunkBounds;
        private GaussianMeshBinding _buffersFor;
        private Matrix4x4[] _boneMatrixData;

        // Per binding mesh, found by transform path below source: the
        // renderer, and the transforms its bind poses refer to
        private Renderer[] _meshRenderers;
        private Transform[][] _meshBones;
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
                _kernelBounds = _shader.FindKernel("CSDriveChunkBounds");
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

        // Re-evaluates the splats on the current bone transforms. Runs on its
        // own (see above); call it after moving bones at another point.
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
                    Transform bone = info.skinned ? _meshBones[m][b] : _meshRenderers[m].transform;
                    _boneMatrixData[info.firstBone + b] = worldToObject * bone.localToWorldMatrix * binding.bindposes[info.firstBone + b];
                }
            }
            _boneMatrices.SetData(_boneMatrixData);

            _shader.SetBuffer(_kernelDrive, PropEntries, _entries);
            _shader.SetBuffer(_kernelDrive, PropVertices, _vertices);
            _shader.SetBuffer(_kernelDrive, PropBoneMatrices, _boneMatrices);
            _shader.SetBuffer(_kernelDrive, PropDeformedBase, _deformedBase);
            _shader.SetFloat(PropHandedness, worldToObject.determinant < 0 ? -1f : 1f);
            foreach (GaussianMeshBinding.MeshInfo info in binding.meshes)
            {
                if (info.entryCount == 0)
                    continue;
                _shader.SetInt(PropFirstEntry, info.firstEntry);
                _shader.SetInt(PropEntryCount, info.entryCount);
                _shader.SetInt(PropFirstVertex, info.firstVertex);
                _shader.SetInt(PropFirstBone, info.firstBone);
                _shader.Dispatch(_kernelDrive, (info.entryCount + GroupSize - 1) / GroupSize, 1, 1);
            }

            int splatCount = binding.splatCount;
            int chunkCount = (splatCount + ChunkSize - 1) / ChunkSize;
            _shader.SetBuffer(_kernelBounds, PropDeformedBase, _deformedBase);
            _shader.SetBuffer(_kernelBounds, PropChunkBounds, _chunkBounds);
            _shader.SetInt(PropSplatCount, splatCount);
            _shader.SetInt(PropChunkCount, chunkCount);
            _shader.Dispatch(_kernelBounds, chunkCount, 1, 1);

            _renderer.SetDeformedBase(_deformedBase, _chunkBounds);
            IsDriving = true;
        }

        private bool CanDrive()
        {
            if (_shader == null || _kernelDrive < 0 || binding == null || source == null)
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
                _renderer.ClearDeformedBase();
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

            var renderers = new Renderer[binding.meshes.Length];
            var bones = new Transform[binding.meshes.Length][];
            for (int m = 0; m < binding.meshes.Length; m++)
            {
                GaussianMeshBinding.MeshInfo info = binding.meshes[m];
                Transform t = string.IsNullOrEmpty(info.path) ? source : source.Find(info.path);
                renderers[m] = t != null ? t.GetComponent<Renderer>() : null;
                if (renderers[m] == null)
                    return Mismatch($"no renderer at {info.path}");
                if (!info.skinned)
                    continue;
                if (renderers[m] is not SkinnedMeshRenderer skinned)
                    return Mismatch($"{info.path} is not a skinned mesh");
                bones[m] = skinned.bones;
                if (bones[m].Length != info.boneCount || System.Array.IndexOf(bones[m], null) >= 0)
                    return Mismatch($"{info.path} has {bones[m].Length} bones or some are missing; the binding has {info.boneCount}");
            }
            _meshRenderers = renderers;
            _meshBones = bones;
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
            _buffersFor = binding;
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
            _chunkBounds?.Dispose();
            _chunkBounds = null;
            _buffersFor = null;
        }
    }
}
