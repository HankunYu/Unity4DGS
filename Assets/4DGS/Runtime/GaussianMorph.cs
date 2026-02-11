// SPDX-License-Identifier: MIT

using Unity.Collections.LowLevel.Unsafe;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

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

        // Cached state for change detection
        GaussianSplatAsset m_CachedTarget;
        int m_KernelMorph = -1;
        int m_MorphedSplatCount;

        const int kGroupSize = 64;

        static readonly ProfilerMarker s_ProfMorph = new ProfilerMarker("GaussianMorph.Dispatch");

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

        void OnEnable()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
            if (morphShader != null)
                m_KernelMorph = morphShader.FindKernel("CSMorphSplats");
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
                m_KernelMorph = morphShader.FindKernel("CSMorphSplats");
                if (m_KernelMorph < 0)
                {
                    DisableMorph();
                    return;
                }
            }

            EnsureTargetBuffers();
            EnsureMorphOutput();
            DispatchMorph();

            m_Renderer.SetMorphData(m_MorphOutput, m_MorphedSplatCount, 1);
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
        }

        void ReleaseAllResources()
        {
            ReleaseTargetBuffers();
            m_MorphOutput?.Dispose();
            m_MorphOutput = null;
            m_MorphedSplatCount = 0;
        }
    }
}
