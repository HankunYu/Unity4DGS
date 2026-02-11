// SPDX-License-Identifier: MIT

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
    /// (position, rotation, scale, opacity, color), and feeds the result to
    /// the renderer via SetMorphData.
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [ExecuteInEditMode]
    [DefaultExecutionOrder(-50)]
    public class GaussianMorph : MonoBehaviour
    {
        [Tooltip("Target asset to morph into")]
        public GaussianSplatAsset targetAsset;

        [Range(0f, 1f)]
        [Tooltip("Blend weight: 0 = source, 1 = target")]
        public float weight;

        [Tooltip("Reference to GaussianMorph.compute")]
        public ComputeShader morphShader;

        [Tooltip("Use spatial nearest-neighbor matching instead of index matching")]
        public bool useNearestMatch = true;

        // Cached renderer reference
        GaussianSplatRenderer m_Renderer;

        // Target asset GPU buffers
        GraphicsBuffer m_TgtPosData;
        GraphicsBuffer m_TgtOtherData;
        GraphicsBuffer m_TgtSHData;
        Texture2D m_TgtColorTex;
        GraphicsBuffer m_TgtChunks;
        bool m_TgtChunksValid;

        // Morph output buffer (4 float4 per splat)
        GraphicsBuffer m_MorphOutput;

        // Correspondence state
        GraphicsBuffer m_Correspondence;
        bool m_CorrespondenceValid;
        GpuSorting m_MortonSorter;

        // Cached state for change detection
        GaussianSplatAsset m_CachedTarget;
        int m_KernelMorph = -1;
        int m_KernelMorton = -1;
        int m_KernelCorrespondence = -1;
        int m_MorphedSplatCount;

        const int kGroupSize = 64;

        static readonly ProfilerMarker s_ProfMorph = new ProfilerMarker("GaussianMorph.Dispatch");
        static readonly ProfilerMarker s_ProfCorrespondence = new ProfilerMarker("GaussianMorph.BuildCorrespondence");

        // Shader property IDs
        static readonly int PropSrcPos = Shader.PropertyToID("_SrcPos");
        static readonly int PropSrcOther = Shader.PropertyToID("_SrcOther");
        static readonly int PropSrcSH = Shader.PropertyToID("_SrcSH");
        static readonly int PropSrcColor = Shader.PropertyToID("_SrcColor");
        static readonly int PropSrcChunks = Shader.PropertyToID("_SrcChunks");
        static readonly int PropSrcFormat = Shader.PropertyToID("_SrcFormat");
        static readonly int PropSrcChunkCount = Shader.PropertyToID("_SrcChunkCount");
        static readonly int PropSrcSplatCount = Shader.PropertyToID("_SrcSplatCount");

        static readonly int PropTgtPos = Shader.PropertyToID("_TgtPos");
        static readonly int PropTgtOther = Shader.PropertyToID("_TgtOther");
        static readonly int PropTgtSH = Shader.PropertyToID("_TgtSH");
        static readonly int PropTgtColor = Shader.PropertyToID("_TgtColor");
        static readonly int PropTgtChunks = Shader.PropertyToID("_TgtChunks");
        static readonly int PropTgtFormat = Shader.PropertyToID("_TgtFormat");
        static readonly int PropTgtChunkCount = Shader.PropertyToID("_TgtChunkCount");
        static readonly int PropTgtSplatCount = Shader.PropertyToID("_TgtSplatCount");

        static readonly int PropMorphWeight = Shader.PropertyToID("_MorphWeight");
        static readonly int PropMorphOutput = Shader.PropertyToID("_MorphOutput");

        static readonly int PropCorrespondence = Shader.PropertyToID("_Correspondence");
        static readonly int PropCorrespondenceOut = Shader.PropertyToID("_CorrespondenceOut");
        static readonly int PropUseCorrespondence = Shader.PropertyToID("_UseCorrespondence");

        static readonly int PropBoundsMin = Shader.PropertyToID("_BoundsMin");
        static readonly int PropBoundsMax = Shader.PropertyToID("_BoundsMax");
        static readonly int PropMortonCodes = Shader.PropertyToID("_MortonCodes");
        static readonly int PropMortonIndices = Shader.PropertyToID("_MortonIndices");
        static readonly int PropSrcSorted = Shader.PropertyToID("_SrcSorted");
        static readonly int PropTgtSorted = Shader.PropertyToID("_TgtSorted");

        static int FindKernelSafe(ComputeShader cs, string name)
        {
            try { return cs.FindKernel(name); }
            catch { return -1; }
        }

        void OnEnable()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
            if (morphShader != null)
            {
                m_KernelMorph = FindKernelSafe(morphShader, "CSMorphSplats");
                m_KernelMorton = FindKernelSafe(morphShader, "CSComputeMorton");
                m_KernelCorrespondence = FindKernelSafe(morphShader, "CSBuildCorrespondence");
            }
        }

        void OnDisable()
        {
            ReleaseAllResources();
            if (m_Renderer != null)
                m_Renderer.SetMorphData(null, 0, 0);
        }

        void LateUpdate()
        {
            // Early-out: invalid renderer or missing requirements
            if (m_Renderer == null || !m_Renderer.HasValidAsset || !m_Renderer.HasValidRenderSetup)
            {
                DisableMorph();
                return;
            }

            if (targetAsset == null || morphShader == null || weight <= 0f)
            {
                DisableMorph();
                return;
            }

            // Ensure kernel is found (shader reference may change at runtime)
            if (m_KernelMorph < 0)
            {
                m_KernelMorph = FindKernelSafe(morphShader, "CSMorphSplats");
                if (m_KernelMorph < 0)
                {
                    DisableMorph();
                    return;
                }
            }

            EnsureTargetBuffers();
            EnsureCorrespondence();
            EnsureMorphOutput();
            DispatchMorph();

            m_Renderer.SetMorphData(m_MorphOutput, m_MorphedSplatCount, 1, weight);
        }

        void DisableMorph()
        {
            if (m_Renderer != null)
                m_Renderer.SetMorphData(null, 0, 0);
        }

        void EnsureTargetBuffers()
        {
            if (m_CachedTarget == targetAsset && m_TgtPosData != null)
                return;

            ReleaseTargetBuffers();

            var tgt = targetAsset;

            // Position data
            m_TgtPosData = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                (int)(tgt.posData.dataSize / 4), 4)
            { name = "MorphTgtPosData" };
            m_TgtPosData.SetData(tgt.posData.GetData<uint>());

            // Other data (rotation + scale)
            m_TgtOtherData = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                (int)(tgt.otherData.dataSize / 4), 4)
            { name = "MorphTgtOtherData" };
            m_TgtOtherData.SetData(tgt.otherData.GetData<uint>());

            // SH data
            m_TgtSHData = new GraphicsBuffer(
                GraphicsBuffer.Target.Raw,
                (int)(tgt.shData.dataSize / 4), 4)
            { name = "MorphTgtSHData" };
            m_TgtSHData.SetData(tgt.shData.GetData<uint>());

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
            m_TgtColorTex = tex;

            // Chunks
            int chunkStride = UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>();
            if (tgt.chunkData != null && tgt.chunkData.dataSize != 0)
            {
                int chunkCount = (int)(tgt.chunkData.dataSize / chunkStride);
                m_TgtChunks = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    chunkCount, chunkStride)
                { name = "MorphTgtChunkData" };
                m_TgtChunks.SetData(tgt.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                m_TgtChunksValid = true;
            }
            else
            {
                // Dummy chunk buffer to avoid null binding
                m_TgtChunks = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    1, chunkStride)
                { name = "MorphTgtChunkData" };
                m_TgtChunksValid = false;
            }

            m_CachedTarget = targetAsset;
        }

        void EnsureCorrespondence()
        {
            if (!useNearestMatch)
            {
                m_CorrespondenceValid = false;
                return;
            }

            if (m_CorrespondenceValid)
                return;

            BuildCorrespondence();
        }

        void BuildCorrespondence()
        {
            using var prof = s_ProfCorrespondence.Auto();

            if (m_Renderer == null || !m_Renderer.HasValidAsset || targetAsset == null)
                return;

            // Ensure Morton/correspondence kernels are found
            if (m_KernelMorton < 0)
                m_KernelMorton = FindKernelSafe(morphShader, "CSComputeMorton");
            if (m_KernelCorrespondence < 0)
                m_KernelCorrespondence = FindKernelSafe(morphShader, "CSBuildCorrespondence");
            if (m_KernelMorton < 0 || m_KernelCorrespondence < 0)
            {
                Debug.LogWarning("GaussianMorph: Morton/Correspondence kernels not found in compute shader. " +
                    "Check for shader compilation errors.");
                return;
            }

            // Create sorter from renderer's radix sort compute shader
            if (m_MortonSorter == null)
            {
                var sortCs = m_Renderer.m_CSSplatUtilities;
                if (sortCs == null) return;
                m_MortonSorter = new GpuSorting(sortCs);
            }
            if (!m_MortonSorter.Valid) return;

            int srcCount = m_Renderer.splatCount;
            int tgtCount = targetAsset.splatCount;
            if (srcCount == 0 || tgtCount == 0) return;

            // Combined AABB from both assets
            var srcAsset = m_Renderer.asset;
            Vector3 bmin = Vector3.Min(srcAsset.boundsMin, targetAsset.boundsMin);
            Vector3 bmax = Vector3.Max(srcAsset.boundsMax, targetAsset.boundsMax);

            // Allocate correspondence buffer
            if (m_Correspondence == null || m_Correspondence.count != srcCount)
            {
                m_Correspondence?.Dispose();
                m_Correspondence = new GraphicsBuffer(GraphicsBuffer.Target.Structured, srcCount, 4)
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

            int mk = m_KernelMorton;
            var cs = morphShader;

            // Pack source format flags (same as morph kernel)
            uint srcFormat = (uint)srcAsset.posFormat |
                             ((uint)srcAsset.scaleFormat << 8) |
                             ((uint)srcAsset.shFormat << 16);

            // --- Compute Morton codes for source (bind source data to _Src* slots) ---
            cmd.SetComputeBufferParam(cs, mk, PropSrcPos, m_Renderer.GpuPosData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcOther, m_Renderer.GpuOtherData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcSH, m_Renderer.GpuSHData);
            cmd.SetComputeTextureParam(cs, mk, PropSrcColor, m_Renderer.GpuColorData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcChunks, m_Renderer.GpuChunksBuffer);
            cmd.SetComputeIntParam(cs, PropSrcFormat, (int)srcFormat);
            cmd.SetComputeIntParam(cs, PropSrcChunkCount,
                m_Renderer.GpuChunksValid ? m_Renderer.GpuChunksBuffer.count : 0);
            cmd.SetComputeIntParam(cs, PropSrcSplatCount, srcCount);
            cmd.SetComputeVectorParam(cs, PropBoundsMin, (Vector4)bmin);
            cmd.SetComputeVectorParam(cs, PropBoundsMax, (Vector4)bmax);
            cmd.SetComputeBufferParam(cs, mk, PropMortonCodes, srcMortonCodes);
            cmd.SetComputeBufferParam(cs, mk, PropMortonIndices, srcMortonIndices);
            cmd.DispatchCompute(cs, mk, (srcCount + kGroupSize - 1) / kGroupSize, 1, 1);

            // Pack target format flags
            uint tgtFormat = (uint)targetAsset.posFormat |
                             ((uint)targetAsset.scaleFormat << 8) |
                             ((uint)targetAsset.shFormat << 16);

            // --- Compute Morton codes for target (rebind target data to _Src* slots) ---
            cmd.SetComputeBufferParam(cs, mk, PropSrcPos, m_TgtPosData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcOther, m_TgtOtherData);
            cmd.SetComputeBufferParam(cs, mk, PropSrcSH, m_TgtSHData);
            cmd.SetComputeTextureParam(cs, mk, PropSrcColor, m_TgtColorTex);
            cmd.SetComputeBufferParam(cs, mk, PropSrcChunks, m_TgtChunks);
            cmd.SetComputeIntParam(cs, PropSrcFormat, (int)tgtFormat);
            cmd.SetComputeIntParam(cs, PropSrcChunkCount,
                m_TgtChunksValid ? m_TgtChunks.count : 0);
            cmd.SetComputeIntParam(cs, PropSrcSplatCount, tgtCount);
            cmd.SetComputeBufferParam(cs, mk, PropMortonCodes, tgtMortonCodes);
            cmd.SetComputeBufferParam(cs, mk, PropMortonIndices, tgtMortonIndices);
            cmd.DispatchCompute(cs, mk, (tgtCount + kGroupSize - 1) / kGroupSize, 1, 1);

            // --- Sort source by Morton code (indices as payload) ---
            var srcSortArgs = new GpuSorting.Args
            {
                count = (uint)srcCount,
                inputKeys = srcMortonCodes,
                inputValues = srcMortonIndices,
                resources = srcSortRes
            };
            m_MortonSorter.Dispatch(cmd, srcSortArgs);

            // --- Sort target by Morton code (indices as payload) ---
            var tgtSortArgs = new GpuSorting.Args
            {
                count = (uint)tgtCount,
                inputKeys = tgtMortonCodes,
                inputValues = tgtMortonIndices,
                resources = tgtSortRes
            };
            m_MortonSorter.Dispatch(cmd, tgtSortArgs);

            // --- Build correspondence from sorted ranks ---
            int ck = m_KernelCorrespondence;
            cmd.SetComputeBufferParam(cs, ck, PropSrcSorted, srcMortonIndices);
            cmd.SetComputeBufferParam(cs, ck, PropTgtSorted, tgtMortonIndices);
            cmd.SetComputeIntParam(cs, PropSrcSplatCount, srcCount);
            cmd.SetComputeIntParam(cs, PropTgtSplatCount, tgtCount);
            cmd.SetComputeBufferParam(cs, ck, PropCorrespondenceOut, m_Correspondence);
            cmd.DispatchCompute(cs, ck, (srcCount + kGroupSize - 1) / kGroupSize, 1, 1);

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

            m_CorrespondenceValid = true;
        }

        void EnsureMorphOutput()
        {
            int srcCount = m_Renderer.splatCount;
            int tgtCount = targetAsset.splatCount;
            int maxCount = Mathf.Max(srcCount, tgtCount);

            if (m_MorphOutput != null && m_MorphedSplatCount == maxCount)
                return;

            m_MorphOutput?.Dispose();
            m_MorphedSplatCount = maxCount;

            // 4 float4 per splat, stride = 16 bytes (sizeof float4)
            m_MorphOutput = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                maxCount * 4, 16)
            { name = "MorphOutput" };
        }

        void DispatchMorph()
        {
            using var prof = s_ProfMorph.Auto();

            var cs = morphShader;
            int kernel = m_KernelMorph;

            var srcAsset = m_Renderer.asset;

            // Bind source buffers
            cs.SetBuffer(kernel, PropSrcPos, m_Renderer.GpuPosData);
            cs.SetBuffer(kernel, PropSrcOther, m_Renderer.GpuOtherData);
            cs.SetBuffer(kernel, PropSrcSH, m_Renderer.GpuSHData);
            cs.SetTexture(kernel, PropSrcColor, m_Renderer.GpuColorData);
            cs.SetBuffer(kernel, PropSrcChunks, m_Renderer.GpuChunksBuffer);

            // Source format flags: posFormat | (scaleFormat << 8) | (shFormat << 16)
            uint srcFormat = (uint)srcAsset.posFormat |
                             ((uint)srcAsset.scaleFormat << 8) |
                             ((uint)srcAsset.shFormat << 16);
            cs.SetInt(PropSrcFormat, (int)srcFormat);
            cs.SetInt(PropSrcChunkCount, m_Renderer.GpuChunksValid ? m_Renderer.GpuChunksBuffer.count : 0);
            cs.SetInt(PropSrcSplatCount, m_Renderer.splatCount);

            // Bind target buffers
            cs.SetBuffer(kernel, PropTgtPos, m_TgtPosData);
            cs.SetBuffer(kernel, PropTgtOther, m_TgtOtherData);
            cs.SetBuffer(kernel, PropTgtSH, m_TgtSHData);
            cs.SetTexture(kernel, PropTgtColor, m_TgtColorTex);
            cs.SetBuffer(kernel, PropTgtChunks, m_TgtChunks);

            // Target format flags
            uint tgtFormat = (uint)targetAsset.posFormat |
                             ((uint)targetAsset.scaleFormat << 8) |
                             ((uint)targetAsset.shFormat << 16);
            cs.SetInt(PropTgtFormat, (int)tgtFormat);
            cs.SetInt(PropTgtChunkCount, m_TgtChunksValid ? m_TgtChunks.count : 0);
            cs.SetInt(PropTgtSplatCount, targetAsset.splatCount);

            // Morph parameters
            cs.SetFloat(PropMorphWeight, weight);
            cs.SetBuffer(kernel, PropMorphOutput, m_MorphOutput);

            // Correspondence
            bool useCor = useNearestMatch && m_CorrespondenceValid && m_Correspondence != null;
            cs.SetInt(PropUseCorrespondence, useCor ? 1 : 0);
            if (m_Correspondence == null)
                m_Correspondence = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4)
                    { name = "MorphCorrespondenceDummy" };
            cs.SetBuffer(kernel, PropCorrespondence, m_Correspondence);

            // Dispatch
            int threadGroups = (m_MorphedSplatCount + kGroupSize - 1) / kGroupSize;
            cs.Dispatch(kernel, threadGroups, 1, 1);
        }

        void ReleaseTargetBuffers()
        {
            m_TgtPosData?.Dispose();
            m_TgtPosData = null;
            m_TgtOtherData?.Dispose();
            m_TgtOtherData = null;
            m_TgtSHData?.Dispose();
            m_TgtSHData = null;

            if (m_TgtColorTex != null)
            {
                DestroyImmediate(m_TgtColorTex);
                m_TgtColorTex = null;
            }

            m_TgtChunks?.Dispose();
            m_TgtChunks = null;
            m_TgtChunksValid = false;

            m_CachedTarget = null;
            m_CorrespondenceValid = false;
        }

        void ReleaseAllResources()
        {
            ReleaseTargetBuffers();
            m_MorphOutput?.Dispose();
            m_MorphOutput = null;
            m_MorphedSplatCount = 0;
            m_Correspondence?.Dispose();
            m_Correspondence = null;
            m_CorrespondenceValid = false;
        }
    }
}
