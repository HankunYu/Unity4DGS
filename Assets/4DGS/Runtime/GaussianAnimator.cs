// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Main controller for procedural splat animation. Attach to the same
    /// GameObject as GaussianSplatRenderer. References scene-placed
    /// GaussianAnimVolume objects, packs their data to GPU, and dispatches
    /// the animation compute shader each frame.
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [ExecuteInEditMode]
    public class GaussianAnimator : MonoBehaviour
    {
        [Tooltip("Volumes that define animation regions. Each volume has modifier components.")]
        public GaussianAnimVolume[] volumes;

        [Tooltip("Animation compute shader (GaussianAnimate.compute)")]
        public ComputeShader animateShader;

        GaussianSplatRenderer m_Renderer;
        GraphicsBuffer m_AnimOutputBuffer;

        // Structured buffers for GPU upload
        GraphicsBuffer m_VolumeBuffer;
        GraphicsBuffer m_ModifierBuffer;

        int m_KernelAnimate = -1;
        int m_LastSplatCount;

        // Cached shader property IDs
        static readonly int PropAnimVolumes = Shader.PropertyToID("_AnimVolumes");
        static readonly int PropAnimModifiers = Shader.PropertyToID("_AnimModifiers");
        static readonly int PropAnimVolumeCount = Shader.PropertyToID("_AnimVolumeCount");
        static readonly int PropAnimModifierCount = Shader.PropertyToID("_AnimModifierCount");
        static readonly int PropAnimSplatCount = Shader.PropertyToID("_AnimSplatCount");
        static readonly int PropAnimSplatFormat = Shader.PropertyToID("_AnimSplatFormat");
        static readonly int PropAnimChunkCount = Shader.PropertyToID("_AnimChunkCount");
        static readonly int PropAnimSplatPos = Shader.PropertyToID("_AnimSplatPos");
        static readonly int PropAnimSplatChunks = Shader.PropertyToID("_AnimSplatChunks");
        static readonly int PropAnimOutput = Shader.PropertyToID("_AnimOutput");
        static readonly int PropAnimMatrixO2W = Shader.PropertyToID("_AnimMatrixObjectToWorld");

        // Must match struct sizes in compute shader
        const int kVolumeSizeBytes = 64 + 16 + 16; // float4x4 + float4 + float4 = 96
        const int kModifierSizeBytes = 16 + 16 + 16 + 16 + 16; // 2 ints + 2 floats + 4 float4s = 80

        const int kMaxVolumes = 16;
        const int kMaxModifiers = 64;

        void OnEnable()
        {
            m_Renderer = GetComponent<GaussianSplatRenderer>();
        }

        void OnDisable()
        {
            ReleaseBuffers();
            if (m_Renderer != null)
                m_Renderer.m_AnimOutputBuffer = null;
        }

        void ReleaseBuffers()
        {
            m_AnimOutputBuffer?.Dispose();
            m_AnimOutputBuffer = null;
            m_VolumeBuffer?.Dispose();
            m_VolumeBuffer = null;
            m_ModifierBuffer?.Dispose();
            m_ModifierBuffer = null;
            m_KernelAnimate = -1;
        }

        void LateUpdate()
        {
            if (m_Renderer == null || !m_Renderer.HasValidAsset || !m_Renderer.HasValidRenderSetup)
            {
                if (m_Renderer != null)
                    m_Renderer.m_AnimOutputBuffer = null;
                return;
            }

            if (animateShader == null)
            {
                m_Renderer.m_AnimOutputBuffer = null;
                return;
            }

            // Collect active volumes and their modifiers
            if (volumes == null || volumes.Length == 0)
            {
                m_Renderer.m_AnimOutputBuffer = null;
                return;
            }

            // Count active volumes and modifiers
            int volumeCount = 0;
            int modifierCount = 0;
            for (int i = 0; i < volumes.Length && volumeCount < kMaxVolumes; i++)
            {
                if (volumes[i] == null || !volumes[i].isActiveAndEnabled) continue;
                volumeCount++;
                var mods = volumes[i].GetModifiers();
                for (int j = 0; j < mods.Length && modifierCount < kMaxModifiers; j++)
                {
                    if (mods[j] != null && mods[j].isActiveAndEnabled)
                        modifierCount++;
                }
            }

            if (volumeCount == 0 || modifierCount == 0)
            {
                m_Renderer.m_AnimOutputBuffer = null;
                return;
            }

            int splatCount = m_Renderer.splatCount;
            EnsureBuffers(splatCount, volumeCount, modifierCount);

            if (m_KernelAnimate < 0)
                return;

            // Upload volume data
            UploadVolumeAndModifierData(volumeCount, modifierCount);

            // Dispatch compute
            DispatchAnimation(splatCount, volumeCount, modifierCount);

            // Set the output buffer on the renderer so CalcViewData can read it
            m_Renderer.m_AnimOutputBuffer = m_AnimOutputBuffer;
        }

        void EnsureBuffers(int splatCount, int volumeCount, int modifierCount)
        {
            // Find kernel
            if (m_KernelAnimate < 0)
            {
                if (!animateShader.HasKernel("CSAnimateSplats"))
                    return;
                m_KernelAnimate = animateShader.FindKernel("CSAnimateSplats");
            }

            // Animation output: 3 float4s per splat
            if (m_AnimOutputBuffer == null || m_LastSplatCount < splatCount)
            {
                m_AnimOutputBuffer?.Dispose();
                m_AnimOutputBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount * 3, 16)
                {
                    name = "GaussianAnimOutput"
                };
                m_LastSplatCount = splatCount;
            }

            // Volume buffer
            if (m_VolumeBuffer == null || m_VolumeBuffer.count < volumeCount)
            {
                m_VolumeBuffer?.Dispose();
                m_VolumeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(volumeCount, 1), kVolumeSizeBytes)
                {
                    name = "GaussianAnimVolumes"
                };
            }

            // Modifier buffer
            if (m_ModifierBuffer == null || m_ModifierBuffer.count < modifierCount)
            {
                m_ModifierBuffer?.Dispose();
                m_ModifierBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(modifierCount, 1), kModifierSizeBytes)
                {
                    name = "GaussianAnimModifiers"
                };
            }
        }

        void UploadVolumeAndModifierData(int volumeCount, int modifierCount)
        {
            // Pack volume data
            var volData = new NativeArray<GaussianAnimVolume.ShaderData>(volumeCount, Allocator.Temp);
            // Pack modifier data
            var modData = new NativeArray<ModifierShaderData>(modifierCount, Allocator.Temp);

            float time = Time.time;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                time = (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif

            int vi = 0;
            int mi = 0;
            for (int i = 0; i < volumes.Length && vi < kMaxVolumes; i++)
            {
                if (volumes[i] == null || !volumes[i].isActiveAndEnabled) continue;
                volData[vi] = volumes[i].GetShaderData();

                var mods = volumes[i].GetModifiers();
                for (int j = 0; j < mods.Length && mi < kMaxModifiers; j++)
                {
                    if (mods[j] == null || !mods[j].isActiveAndEnabled) continue;
                    mods[j].FillParams(time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3);
                    modData[mi] = new ModifierShaderData
                    {
                        volumeIndex = vi,
                        modifierType = mods[j].ModifierType,
                        pad0 = 0,
                        pad1 = 0,
                        params0 = p0,
                        params1 = p1,
                        params2 = p2,
                        params3 = p3
                    };
                    mi++;
                }
                vi++;
            }

            m_VolumeBuffer.SetData(volData, 0, 0, volumeCount);
            m_ModifierBuffer.SetData(modData, 0, 0, modifierCount);

            volData.Dispose();
            modData.Dispose();
        }

        void DispatchAnimation(int splatCount, int volumeCount, int modifierCount)
        {
            var cs = animateShader;
            int kernel = m_KernelAnimate;

            cs.SetBuffer(kernel, PropAnimVolumes, m_VolumeBuffer);
            cs.SetBuffer(kernel, PropAnimModifiers, m_ModifierBuffer);
            cs.SetBuffer(kernel, PropAnimOutput, m_AnimOutputBuffer);
            cs.SetInt(PropAnimVolumeCount, volumeCount);
            cs.SetInt(PropAnimModifierCount, modifierCount);
            cs.SetInt(PropAnimSplatCount, splatCount);

            var asset = m_Renderer.asset;
            uint format = (uint)asset.posFormat | ((uint)asset.scaleFormat << 8) | ((uint)asset.shFormat << 16);
            cs.SetInt(PropAnimSplatFormat, (int)format);

            bool hasChunks = m_Renderer.m_GpuChunksValid;
            int chunkCount = hasChunks ? m_Renderer.m_GpuChunks.count : 0;
            cs.SetInt(PropAnimChunkCount, chunkCount);

            // Set original pos buffer and chunk data
            cs.SetBuffer(kernel, PropAnimSplatPos, m_Renderer.GpuPosData);
            cs.SetBuffer(kernel, PropAnimSplatChunks, m_Renderer.m_GpuChunks);

            cs.SetMatrix(PropAnimMatrixO2W, transform.localToWorldMatrix);

            cs.GetKernelThreadGroupSizes(kernel, out uint gsX, out _, out _);
            cs.Dispatch(kernel, (splatCount + (int)gsX - 1) / (int)gsX, 1, 1);
        }

        // Must match AnimModifierData in compute shader
        struct ModifierShaderData
        {
            public int volumeIndex;
            public int modifierType;
            public float pad0;
            public float pad1;
            public Vector4 params0;
            public Vector4 params1;
            public Vector4 params2;
            public Vector4 params3;
        }
    }
}
