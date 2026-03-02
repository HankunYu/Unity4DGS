using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Drives GPU-based morph transitions between two Gaussian Splat assets.
    /// Decodes both source and target on the GPU, blends per-splat properties
    /// (position, rotation, scale, opacity, color, SH), and feeds the result to
    /// the renderer via SetMorphData.
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [ExecuteInEditMode]
    [DefaultExecutionOrder(-50)]
    public class GaussianMorph : MonoBehaviour
    {
        [SerializeField] [Tooltip("Target asset to morph into")]
        private GaussianSplatAsset _targetAsset;

        [SerializeField] [Range(0f, 1f)] [Tooltip("Blend weight: 0 = source, 1 = target")]
        private float _weight;

        [SerializeField] [Tooltip("Reference to GaussianMorph.compute")]
        private ComputeShader _morphShader;

        [SerializeField] [Tooltip("Use spatial nearest-neighbor matching instead of index matching")]
        private bool _useNearestMatch = true;

        // Public accessors
        public GaussianSplatAsset TargetAsset { get => _targetAsset; set => _targetAsset = value; }
        public float Weight { get => _weight; set => _weight = value; }
        public ComputeShader MorphShader { get => _morphShader; set => _morphShader = value; }
        public bool UseNearestMatch { get => _useNearestMatch; set => _useNearestMatch = value; }

        // Cached renderer reference
        private GaussianSplatRenderer _renderer;

        // Target asset GPU buffers
        private GraphicsBuffer _tgtPosData;
        private GraphicsBuffer _tgtOtherData;
        private GraphicsBuffer _tgtSHData;
        private Texture2D _tgtColorTex;
        private GraphicsBuffer _tgtChunks;
        private bool _tgtChunksValid;

        // Morph output buffer (4 float4 per splat)
        private GraphicsBuffer _morphOutput;

        // Morph SH output buffer (96 bytes per splat, fp16 packed)
        private GraphicsBuffer _morphSHOutput;

        // Correspondence state
        private GraphicsBuffer _correspondence;
        private bool _correspondenceValid;
        private GpuSorting _mortonSorter;

        // Cached state for change detection
        private GaussianSplatAsset _cachedTarget;
        private int _kernelMorph = -1;
        private int _kernelMorton = -1;
        private int _kernelCorrespondence = -1;
        private int _morphedSplatCount;

        private const int GroupSize = 64;

        private static readonly ProfilerMarker ProfMorph = new ProfilerMarker("GaussianMorph.Dispatch");
        private static readonly ProfilerMarker ProfCorrespondence = new ProfilerMarker("GaussianMorph.BuildCorrespondence");

        // Shader property IDs
        private static readonly int PropSrcPos = Shader.PropertyToID("_SrcPos");
        private static readonly int PropSrcOther = Shader.PropertyToID("_SrcOther");
        private static readonly int PropSrcSH = Shader.PropertyToID("_SrcSH");
        private static readonly int PropSrcColor = Shader.PropertyToID("_SrcColor");
        private static readonly int PropSrcChunks = Shader.PropertyToID("_SrcChunks");
        private static readonly int PropSrcFormat = Shader.PropertyToID("_SrcFormat");
        private static readonly int PropSrcChunkCount = Shader.PropertyToID("_SrcChunkCount");
        private static readonly int PropSrcSplatCount = Shader.PropertyToID("_SrcSplatCount");

        private static readonly int PropTgtPos = Shader.PropertyToID("_TgtPos");
        private static readonly int PropTgtOther = Shader.PropertyToID("_TgtOther");
        private static readonly int PropTgtSH = Shader.PropertyToID("_TgtSH");
        private static readonly int PropTgtColor = Shader.PropertyToID("_TgtColor");
        private static readonly int PropTgtChunks = Shader.PropertyToID("_TgtChunks");
        private static readonly int PropTgtFormat = Shader.PropertyToID("_TgtFormat");
        private static readonly int PropTgtChunkCount = Shader.PropertyToID("_TgtChunkCount");
        private static readonly int PropTgtSplatCount = Shader.PropertyToID("_TgtSplatCount");

        private static readonly int PropMorphWeight = Shader.PropertyToID("_MorphWeight");
        private static readonly int PropMorphOutput = Shader.PropertyToID("_MorphOutput");
        private static readonly int PropMorphSHOutput = Shader.PropertyToID("_MorphSHOutput");

        private static readonly int PropCorrespondence = Shader.PropertyToID("_Correspondence");
        private static readonly int PropCorrespondenceOut = Shader.PropertyToID("_CorrespondenceOut");
        private static readonly int PropUseCorrespondence = Shader.PropertyToID("_UseCorrespondence");

        private static readonly int PropBoundsMin = Shader.PropertyToID("_BoundsMin");
        private static readonly int PropBoundsMax = Shader.PropertyToID("_BoundsMax");
        private static readonly int PropMortonCodes = Shader.PropertyToID("_MortonCodes");
        private static readonly int PropMortonIndices = Shader.PropertyToID("_MortonIndices");
        private static readonly int PropSrcSorted = Shader.PropertyToID("_SrcSorted");
        private static readonly int PropTgtSorted = Shader.PropertyToID("_TgtSorted");

        private static int FindKernelSafe(ComputeShader cs, string name)
        {
            try { return cs.FindKernel(name); }
            catch { return -1; }
        }

        private void OnEnable()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            if (_morphShader != null)
            {
                _kernelMorph = FindKernelSafe(_morphShader, "CSMorphSplats");
                _kernelMorton = FindKernelSafe(_morphShader, "CSComputeMorton");
                _kernelCorrespondence = FindKernelSafe(_morphShader, "CSBuildCorrespondence");
            }
        }

        private void OnDisable()
        {
            ReleaseAllResources();
            if (_renderer != null)
                _renderer.SetMorphData(null, 0, 0);
        }

        private void LateUpdate()
        {
            // Early-out: invalid renderer or missing requirements
            if (_renderer == null || !_renderer.HasValidAsset || !_renderer.HasValidRenderSetup)
            {
                DisableMorph();
                return;
            }

            if (_targetAsset == null || _morphShader == null || _weight <= 0f)
            {
                DisableMorph();
                return;
            }

            // Ensure kernel is found (shader reference may change at runtime)
            if (_kernelMorph < 0)
            {
                _kernelMorph = FindKernelSafe(_morphShader, "CSMorphSplats");
                if (_kernelMorph < 0)
                {
                    DisableMorph();
                    return;
                }
            }

            EnsureTargetBuffers();
            EnsureCorrespondence();
            EnsureMorphOutput();
            DispatchMorph();

            _renderer.SetMorphData(_morphOutput, _morphedSplatCount, 1, _weight, _morphSHOutput);
        }

        private void DisableMorph()
        {
            if (_renderer != null)
                _renderer.SetMorphData(null, 0, 0);
        }

        private void EnsureTargetBuffers()
        {
            if (_cachedTarget == _targetAsset && _tgtPosData != null)
                return;

            ReleaseTargetBuffers();

            var tgt = _targetAsset;

            // Position data
            _tgtPosData = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                (int)(tgt.posData.dataSize / 4), 4)
            { name = "MorphTgtPosData" };
            _tgtPosData.SetData(tgt.posData.GetData<uint>());

            // Other data (rotation + scale)
            _tgtOtherData = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                (int)(tgt.otherData.dataSize / 4), 4)
            { name = "MorphTgtOtherData" };
            _tgtOtherData.SetData(tgt.otherData.GetData<uint>());

            // SH data
            _tgtSHData = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                (int)(tgt.shData.dataSize / 4), 4)
            { name = "MorphTgtSHData" };
            _tgtSHData.SetData(tgt.shData.GetData<uint>());

            // Color texture
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(tgt.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(tgt.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat,
                TextureCreationFlags.DontInitializePixels |
                TextureCreationFlags.IgnoreMipmapLimit |
                TextureCreationFlags.DontUploadUponCreate)
            { name = "MorphTgtColorData" };
            tex.SetPixelData(tgt.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            _tgtColorTex = tex;

            // Chunks
            int chunkStride = UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();
            if (tgt.chunkData != null && tgt.chunkData.dataSize != 0)
            {
                int chunkCount = (int)(tgt.chunkData.dataSize / chunkStride);
                _tgtChunks = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    chunkCount, chunkStride)
                { name = "MorphTgtChunkData" };
                _tgtChunks.SetData(tgt.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                _tgtChunksValid = true;
            }
            else
            {
                // Dummy chunk buffer to avoid null binding
                _tgtChunks = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    1, chunkStride)
                { name = "MorphTgtChunkData" };
                _tgtChunksValid = false;
            }

            _cachedTarget = _targetAsset;
        }

        private void EnsureCorrespondence()
        {
            if (!_useNearestMatch)
            {
                _correspondenceValid = false;
                return;
            }

            if (_correspondenceValid)
                return;

            BuildCorrespondence();
        }

        private void BuildCorrespondence()
        {
            using var prof = ProfCorrespondence.Auto();

            if (_renderer == null || !_renderer.HasValidAsset || _targetAsset == null)
                return;

            // Ensure Morton/correspondence kernels are found
            if (_kernelMorton < 0)
                _kernelMorton = FindKernelSafe(_morphShader, "CSComputeMorton");
            if (_kernelCorrespondence < 0)
                _kernelCorrespondence = FindKernelSafe(_morphShader, "CSBuildCorrespondence");
            if (_kernelMorton < 0 || _kernelCorrespondence < 0)
            {
                Debug.LogWarning("GaussianMorph: Morton/Correspondence kernels not found in compute shader. " +
                    "Check for shader compilation errors.");
                return;
            }

            // Create sorter from renderer's radix sort compute shader
            if (_mortonSorter == null)
            {
                var sortCs = _renderer.csSplatUtilities;
                if (sortCs == null) return;
                _mortonSorter = new GpuSorting(sortCs);
            }
            if (!_mortonSorter.Valid) return;

            int srcCount = _renderer.splatCount;
            int tgtCount = _targetAsset.splatCount;
            if (srcCount == 0 || tgtCount == 0) return;

            // Combined AABB from both assets
            var srcAsset = _renderer.asset;
            Vector3 bmin = Vector3.Min(srcAsset.boundsMin, _targetAsset.boundsMin);
            Vector3 bmax = Vector3.Max(srcAsset.boundsMax, _targetAsset.boundsMax);

            // Allocate correspondence buffer
            if (_correspondence == null || _correspondence.count != srcCount)
            {
                _correspondence?.Dispose();
                _correspondence = new GraphicsBuffer(GraphicsBuffer.Target.Structured, srcCount, 4)
                    { name = "MorphCorrespondence" };
            }

            // Allocate temporary Morton code + index buffers
            var srcMortonCodes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, srcCount, 4)
                { name = "MorphSrcMortonCodes" };
            var srcMortonIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, srcCount, 4)
                { name = "MorphSrcMortonIndices" };
            var tgtMortonCodes = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tgtCount, 4)
                { name = "MorphTgtMortonCodes" };
            var tgtMortonIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, tgtCount, 4)
                { name = "MorphTgtMortonIndices" };

            // Allocate sort support resources
            var srcSortRes = GpuSorting.SupportResources.Load((uint)srcCount);
            var tgtSortRes = GpuSorting.SupportResources.Load((uint)tgtCount);

            var cmd = new CommandBuffer { name = "MorphBuildCorrespondence" };

            int mk = _kernelMorton;
            var cs = _morphShader;

            // Pack source format flags (same as morph kernel)
            uint srcFormat = (uint)srcAsset.posFormat |
                             ((uint)srcAsset.scaleFormat << 8) |
                             ((uint)srcAsset.shFormat << 16);

            // --- Compute Morton codes for source (bind source data to _Src* slots) ---
            cmd.SetComputeBufferParam(cs, mk, PropSrcPos, _renderer.GpuPosData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcOther, _renderer.GpuOtherData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcSH, _renderer.GpuSHData);
            cmd.SetComputeTextureParam(cs, mk, PropSrcColor, _renderer.GpuColorData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcChunks, _renderer.GpuChunksBuffer);
            cmd.SetComputeIntParam(cs, PropSrcFormat, (int)srcFormat);
            cmd.SetComputeIntParam(cs, PropSrcChunkCount,
                _renderer.GpuChunksValid ? _renderer.GpuChunksBuffer.count : 0);
            cmd.SetComputeIntParam(cs, PropSrcSplatCount, srcCount);
            cmd.SetComputeVectorParam(cs, PropBoundsMin, (Vector4)bmin);
            cmd.SetComputeVectorParam(cs, PropBoundsMax, (Vector4)bmax);
            cmd.SetComputeBufferParam(cs, mk, PropMortonCodes, srcMortonCodes);
            cmd.SetComputeBufferParam(cs, mk, PropMortonIndices, srcMortonIndices);
            cmd.DispatchCompute(cs, mk, (srcCount + GroupSize - 1) / GroupSize, 1, 1);

            // Pack target format flags
            uint tgtFormat = (uint)_targetAsset.posFormat |
                             ((uint)_targetAsset.scaleFormat << 8) |
                             ((uint)_targetAsset.shFormat << 16);

            // --- Compute Morton codes for target (rebind target data to _Src* slots) ---
            cmd.SetComputeBufferParam(cs, mk, PropSrcPos, _tgtPosData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcOther, _tgtOtherData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcSH, _tgtSHData);
            cmd.SetComputeTextureParam(cs, mk, PropSrcColor, _tgtColorTex);
            cmd.SetComputeBufferParam(cs, mk, PropSrcChunks, _tgtChunks);
            cmd.SetComputeIntParam(cs, PropSrcFormat, (int)tgtFormat);
            cmd.SetComputeIntParam(cs, PropSrcChunkCount,
                _tgtChunksValid ? _tgtChunks.count : 0);
            cmd.SetComputeIntParam(cs, PropSrcSplatCount, tgtCount);
            cmd.SetComputeBufferParam(cs, mk, PropMortonCodes, tgtMortonCodes);
            cmd.SetComputeBufferParam(cs, mk, PropMortonIndices, tgtMortonIndices);
            cmd.DispatchCompute(cs, mk, (tgtCount + GroupSize - 1) / GroupSize, 1, 1);

            // --- Sort source by Morton code ---
            var srcSortArgs = new GpuSorting.Args
            {
                count = (uint)srcCount,
                inputKeys = srcMortonCodes,
                inputValues = srcMortonIndices,
                resources = srcSortRes
            };
            _mortonSorter.Dispatch(cmd, srcSortArgs);

            // --- Sort target by Morton code (indices as payload) ---
            var tgtSortArgs = new GpuSorting.Args
            {
                count = (uint)tgtCount,
                inputKeys = tgtMortonCodes,
                inputValues = tgtMortonIndices,
                resources = tgtSortRes
            };
            _mortonSorter.Dispatch(cmd, tgtSortArgs);

            // --- Build correspondence from sorted ranks ---
            int ck = _kernelCorrespondence;
            cmd.SetComputeBufferParam(cs, ck, PropSrcSorted, srcMortonIndices);
            cmd.SetComputeBufferParam(cs, ck, PropTgtSorted, tgtMortonIndices);
            cmd.SetComputeIntParam(cs, PropSrcSplatCount, srcCount);
            cmd.SetComputeIntParam(cs, PropTgtSplatCount, tgtCount);
            cmd.SetComputeBufferParam(cs, ck, PropCorrespondenceOut, _correspondence);
            cmd.DispatchCompute(cs, ck, (srcCount + GroupSize - 1) / GroupSize, 1, 1);

            // Execute all GPU commands
            Graphics.ExecuteCommandBuffer(cmd);
            cmd.Dispose();

            // Release temporary buffers (GPU driver tracks in-flight references)
            srcMortonCodes.Dispose();
            srcMortonIndices.Dispose();
            tgtMortonCodes.Dispose();
            tgtMortonIndices.Dispose();
            srcSortRes.Dispose();
            tgtSortRes.Dispose();

            _correspondenceValid = true;
        }

        private void EnsureMorphOutput()
        {
            int srcCount = _renderer.splatCount;
            int tgtCount = _targetAsset.splatCount;
            int maxCount = Mathf.Max(srcCount, tgtCount);

            if (_morphOutput != null && _morphedSplatCount == maxCount)
                return;

            _morphOutput?.Dispose();
            _morphSHOutput?.Dispose();
            _morphedSplatCount = maxCount;

            // 4 float4 per splat, stride = 16 bytes (sizeof float4)
            _morphOutput = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                maxCount * 4, 16)
            { name = "MorphOutput" };

            // SH buffer: 96 bytes per splat (15 half3 in fp16 packed format)
            // Using Raw target for ByteAddressBuffer access in compute shader
            _morphSHOutput = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                maxCount * 24, 4)
            { name = "MorphSHOutput" };
        }

        private void DispatchMorph()
        {
            using var prof = ProfMorph.Auto();

            var cs = _morphShader;
            int kernel = _kernelMorph;

            var srcAsset = _renderer.asset;

            // Bind source buffers
            cs.SetBuffer(kernel, PropSrcPos, _renderer.GpuPosData);
            cs.SetBuffer(kernel, PropSrcOther, _renderer.GpuOtherData);
            cs.SetBuffer(kernel, PropSrcSH, _renderer.GpuSHData);
            cs.SetTexture(kernel, PropSrcColor, _renderer.GpuColorData);
            cs.SetBuffer(kernel, PropSrcChunks, _renderer.GpuChunksBuffer);

            // Source format flags: posFormat | (scaleFormat << 8) | (shFormat << 16)
            uint srcFormat = (uint)srcAsset.posFormat |
                             ((uint)srcAsset.scaleFormat << 8) |
                             ((uint)srcAsset.shFormat << 16);
            cs.SetInt(PropSrcFormat, (int)srcFormat);
            cs.SetInt(PropSrcChunkCount, _renderer.GpuChunksValid ? _renderer.GpuChunksBuffer.count : 0);
            cs.SetInt(PropSrcSplatCount, _renderer.splatCount);

            // Bind target buffers
            cs.SetBuffer(kernel, PropTgtPos, _tgtPosData);
            cs.SetBuffer(kernel, PropTgtOther, _tgtOtherData);
            cs.SetBuffer(kernel, PropTgtSH, _tgtSHData);
            cs.SetTexture(kernel, PropTgtColor, _tgtColorTex);
            cs.SetBuffer(kernel, PropTgtChunks, _tgtChunks);

            // Target format flags
            uint tgtFormat = (uint)_targetAsset.posFormat |
                             ((uint)_targetAsset.scaleFormat << 8) |
                             ((uint)_targetAsset.shFormat << 16);
            cs.SetInt(PropTgtFormat, (int)tgtFormat);
            cs.SetInt(PropTgtChunkCount, _tgtChunksValid ? _tgtChunks.count : 0);
            cs.SetInt(PropTgtSplatCount, _targetAsset.splatCount);

            // Morph parameters
            cs.SetFloat(PropMorphWeight, _weight);
            cs.SetBuffer(kernel, PropMorphOutput, _morphOutput);
            cs.SetBuffer(kernel, PropMorphSHOutput, _morphSHOutput);

            // Correspondence
            bool useCor = _useNearestMatch && _correspondenceValid && _correspondence != null;
            cs.SetInt(PropUseCorrespondence, useCor ? 1 : 0);
            if (_correspondence == null)
                _correspondence = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4)
                    { name = "MorphCorrespondenceDummy" };
            cs.SetBuffer(kernel, PropCorrespondence, _correspondence);

            // Dispatch
            int threadGroups = (_morphedSplatCount + GroupSize - 1) / GroupSize;
            cs.Dispatch(kernel, threadGroups, 1, 1);
        }

        private void ReleaseTargetBuffers()
        {
            _tgtPosData?.Dispose();
            _tgtPosData = null;
            _tgtOtherData?.Dispose();
            _tgtOtherData = null;
            _tgtSHData?.Dispose();
            _tgtSHData = null;

            if (_tgtColorTex != null)
            {
                DestroyImmediate(_tgtColorTex);
                _tgtColorTex = null;
            }

            _tgtChunks?.Dispose();
            _tgtChunks = null;
            _tgtChunksValid = false;

            _cachedTarget = null;
            _correspondenceValid = false;
        }

        private void ReleaseAllResources()
        {
            ReleaseTargetBuffers();
            _morphOutput?.Dispose();
            _morphOutput = null;
            _morphSHOutput?.Dispose();
            _morphSHOutput = null;
            _morphedSplatCount = 0;
            _correspondence?.Dispose();
            _correspondence = null;
            _correspondenceValid = false;
        }
    }
}
