// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

#if !UNITY_6000_0_OR_NEWER
#error Unity Gaussian Splatting URP support only works in Unity 6 or later
#endif

using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;

namespace GaussianSplatting.Runtime
{
    // Note: I have no idea what is the purpose of ScriptableRendererFeature vs ScriptableRenderPass, which one of those
    // is supposed to do resource management vs logic, etc. etc. Code below "seems to work" but I'm just fumbling along,
    // without understanding any of it.
    //
    // ReSharper disable once InconsistentNaming
    class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        enum StylizeTarget
        {
            GaussianOnly = 0,
            Fullscreen = 1
        }

        [Serializable]
        struct StylizeSettings
        {
            public float grainIntensity;
            public float grainScale;
            public float grainTemporalJitter;
            public float vintageStrength;
            public float posterizeLevels;
            public float shadowTintToGreen;
            public float highlightWarmth;
            public float vignette;
            public float blend;
        }

        const string StylizeShaderName = "Hidden/Gaussian Splatting/Stylize";
        static readonly int s_stylizeSourceTex = Shader.PropertyToID("_StylizeSourceTex");
        static readonly int s_grainIntensity = Shader.PropertyToID("_GrainIntensity");
        static readonly int s_grainScale = Shader.PropertyToID("_GrainScale");
        static readonly int s_grainTemporalJitter = Shader.PropertyToID("_GrainTemporalJitter");
        static readonly int s_vintageStrength = Shader.PropertyToID("_VintageStrength");
        static readonly int s_posterizeLevels = Shader.PropertyToID("_PosterizeLevels");
        static readonly int s_shadowTintToGreen = Shader.PropertyToID("_ShadowTintToGreen");
        static readonly int s_highlightWarmth = Shader.PropertyToID("_HighlightWarmth");
        static readonly int s_vignetteStrength = Shader.PropertyToID("_VignetteStrength");
        static readonly int s_effectBlend = Shader.PropertyToID("_EffectBlend");

        [SerializeField] StylizeTarget m_StylizeTarget = StylizeTarget.GaussianOnly;
        [SerializeField] RenderPassEvent m_StylizeEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        [SerializeField] Shader m_StylizeShader;

        Material m_StylizeMaterial;
        bool m_LoggedMissingStylizeShader;

        class GSRenderPass : ScriptableRenderPass
        {
            const string GaussianSplatRTName = "_GaussianSplatRT";
            const string GaussianStylizeRTName = "_GaussianStylizedRT";

            const string ProfilerTag = "GaussianSplatRenderGraph";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);
            static readonly int s_gaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);

            readonly GaussianSplatURPFeature m_Owner;

            class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                internal TextureHandle StylizedGaussianRT;
                internal bool ApplyStylize;
                internal StylizeSettings StylizeSettings;
                internal Material StylizeMaterial;
            }

            public GSRenderPass(GaussianSplatURPFeature owner)
            {
                m_Owner = owner;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                TextureHandle textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);
                bool applyStylize =
                    m_Owner.CanRunStylizeForCamera(cameraData.camera, StylizeTarget.GaussianOnly, out var stylizeSettings);
                TextureHandle stylizedHandle = textureHandle;
                if (applyStylize)
                    stylizedHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianStylizeRTName, true);

                passData.CameraData = cameraData;
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                passData.GaussianSplatRT = textureHandle;
                passData.StylizedGaussianRT = stylizedHandle;
                passData.ApplyStylize = applyStylize;
                passData.StylizeSettings = stylizeSettings;
                passData.StylizeMaterial = m_Owner.m_StylizeMaterial;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture);
                builder.UseTexture(textureHandle, AccessFlags.ReadWrite);
                if (applyStylize)
                    builder.UseTexture(stylizedHandle, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                    commandBuffer.SetGlobalTexture(s_gaussianSplatRT, data.GaussianSplatRT);
                    CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.Color, Color.clear);
                    Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer);
                    if (matComposite != null)
                    {
                        TextureHandle composeSource = data.GaussianSplatRT;
                        if (data.ApplyStylize && data.StylizeMaterial != null)
                        {
                            SetStylizeMaterialProperties(data.StylizeMaterial, in data.StylizeSettings);
                            commandBuffer.SetGlobalTexture(s_stylizeSourceTex, data.GaussianSplatRT);
                            Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.StylizedGaussianRT, data.StylizeMaterial, 0);
                            composeSource = data.StylizedGaussianRT;
                        }

                        commandBuffer.SetGlobalTexture(s_gaussianSplatRT, composeSource);
                        commandBuffer.BeginSample(GaussianSplatRenderSystem.s_ProfCompose);
                        Blitter.BlitCameraTexture(commandBuffer, composeSource, data.SourceTexture, matComposite, 0);
                        commandBuffer.EndSample(GaussianSplatRenderSystem.s_ProfCompose);
                    }
                });
            }
        }

        class FullscreenStylizePass : ScriptableRenderPass
        {
            const string FullscreenStylizeRTName = "_GaussianFullscreenStylizeRT";
            const string ProfilerTag = "GaussianStylizeFullscreen";
            static readonly ProfilingSampler s_profilingSampler = new(ProfilerTag);

            readonly GaussianSplatURPFeature m_Owner;

            class PassData
            {
                internal TextureHandle SourceTexture;
                internal TextureHandle TempTexture;
                internal StylizeSettings StylizeSettings;
                internal Material StylizeMaterial;
            }

            public FullscreenStylizePass(GaussianSplatURPFeature owner)
            {
                m_Owner = owner;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!m_Owner.CanRunStylizeForCamera(cameraData.camera, StylizeTarget.Fullscreen, out var stylizeSettings))
                    return;

                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                TextureHandle tempTexture =
                    UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, FullscreenStylizeRTName, true);

                passData.SourceTexture = resourceData.activeColorTexture;
                passData.TempTexture = tempTexture;
                passData.StylizeSettings = stylizeSettings;
                passData.StylizeMaterial = m_Owner.m_StylizeMaterial;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(tempTexture, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    if (data.StylizeMaterial == null)
                        return;

                    CommandBuffer commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, s_profilingSampler);
                    SetStylizeMaterialProperties(data.StylizeMaterial, in data.StylizeSettings);
                    commandBuffer.SetGlobalTexture(s_stylizeSourceTex, data.SourceTexture);
                    Blitter.BlitCameraTexture(commandBuffer, data.SourceTexture, data.TempTexture, data.StylizeMaterial, 0);
                    commandBuffer.SetGlobalTexture(s_stylizeSourceTex, data.TempTexture);
                    Blitter.BlitCameraTexture(commandBuffer, data.TempTexture, data.SourceTexture, data.StylizeMaterial, 1);
                });
            }
        }

        GSRenderPass m_Pass;
        FullscreenStylizePass m_FullscreenStylizePass;
        bool m_HasCamera;

        public override void Create()
        {
            EnsureStylizeMaterial();

            m_Pass = new GSRenderPass(this)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
            m_FullscreenStylizePass = new FullscreenStylizePass(this)
            {
                renderPassEvent = m_StylizeEvent
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            m_HasCamera = false;
            if (!IsSupportedCamera(cameraData.camera))
                return;

            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            m_HasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            EnsureStylizeMaterial();
            if (m_HasCamera)
                renderer.EnqueuePass(m_Pass);

            if (m_StylizeTarget == StylizeTarget.Fullscreen && IsSupportedCamera(renderingData.cameraData.camera))
            {
                m_FullscreenStylizePass.renderPassEvent = m_StylizeEvent;
                renderer.EnqueuePass(m_FullscreenStylizePass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            m_Pass = null;
            m_FullscreenStylizePass = null;
            CoreUtils.Destroy(m_StylizeMaterial);
            m_StylizeMaterial = null;
        }

        void EnsureStylizeMaterial()
        {
            if (m_StylizeMaterial != null)
                return;

            if (m_StylizeShader == null)
                m_StylizeShader = Shader.Find(StylizeShaderName);
            if (m_StylizeShader == null)
            {
                if (!m_LoggedMissingStylizeShader)
                {
                    Debug.LogWarning(
                        "Gaussian Stylize shader not found. Assign a shader in GaussianSplatURPFeature or include Hidden/Gaussian Splatting/Stylize.");
                    m_LoggedMissingStylizeShader = true;
                }
                return;
            }

            m_StylizeMaterial = CoreUtils.CreateEngineMaterial(m_StylizeShader);
            m_LoggedMissingStylizeShader = false;
        }

        bool CanRunStylizeForCamera(Camera camera, StylizeTarget target, out StylizeSettings settings)
        {
            settings = default;
            if (m_StylizeTarget != target)
                return false;
            if (!IsSupportedCamera(camera))
                return false;
            if (m_StylizeMaterial == null)
                return false;
            return TryGetStylizeSettings(out settings);
        }

        bool TryGetStylizeSettings(out StylizeSettings settings)
        {
            settings = default;
            VolumeStack volumeStack = VolumeManager.instance?.stack;
            if (volumeStack == null)
                return false;

            GaussianStylizeVolume stylizeVolume = volumeStack.GetComponent<GaussianStylizeVolume>();
            if (stylizeVolume == null || !stylizeVolume.IsActive())
                return false;

            settings = new StylizeSettings
            {
                grainIntensity = stylizeVolume.grainIntensity.value,
                grainScale = stylizeVolume.grainScale.value,
                grainTemporalJitter = stylizeVolume.grainTemporalJitter.value,
                vintageStrength = stylizeVolume.vintageStrength.value,
                posterizeLevels = stylizeVolume.posterizeLevels.value,
                shadowTintToGreen = stylizeVolume.shadowTintToGreen.value,
                highlightWarmth = stylizeVolume.highlightWarmth.value,
                vignette = stylizeVolume.vignette.value,
                blend = stylizeVolume.blend.value
            };
            return true;
        }

        static bool IsSupportedCamera(Camera camera)
        {
            if (camera == null)
                return false;
            return camera.cameraType != CameraType.Preview && camera.cameraType != CameraType.Reflection;
        }

        static void SetStylizeMaterialProperties(Material material, in StylizeSettings settings)
        {
            material.SetFloat(s_grainIntensity, settings.grainIntensity);
            material.SetFloat(s_grainScale, settings.grainScale);
            material.SetFloat(s_grainTemporalJitter, settings.grainTemporalJitter);
            material.SetFloat(s_vintageStrength, settings.vintageStrength);
            material.SetFloat(s_posterizeLevels, settings.posterizeLevels);
            material.SetFloat(s_shadowTintToGreen, settings.shadowTintToGreen);
            material.SetFloat(s_highlightWarmth, settings.highlightWarmth);
            material.SetFloat(s_vignetteStrength, settings.vignette);
            material.SetFloat(s_effectBlend, settings.blend);
        }
    }
}

#endif // #if GS_ENABLE_URP
