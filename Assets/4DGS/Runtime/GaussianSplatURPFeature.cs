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
    internal class GaussianSplatURPFeature : ScriptableRendererFeature
    {
        private enum StylizeTarget
        {
            GaussianOnly = 0,
            Fullscreen = 1
        }

        [Serializable]
        private struct StylizeSettings
        {
            public float grainIntensity;
            public float grainScale;
            public float grainTemporalJitter;
            public float vintageStrength;
            public float posterizeLevels;
            public float posterizeMix;
            public float shadowTintToGreen;
            public float highlightWarmth;
            public float vignette;
            public float brushStrength;
            public float brushScale;
            public float brushAngleJitter;
            public float colorMergeStrength;
            public float colorMergeLevels;
            public float colorMergeThreshold;
            public float colorMergeRadius;
            public float colorMergeEdgeProtect;
            public float blend;
        }

        private const string StylizeShaderName = "Hidden/Gaussian Splatting/Stylize";
        private static readonly int StylizeSourceTex = Shader.PropertyToID("_StylizeSourceTex");
        private static readonly int GrainIntensity = Shader.PropertyToID("_GrainIntensity");
        private static readonly int GrainScale = Shader.PropertyToID("_GrainScale");
        private static readonly int GrainTemporalJitter = Shader.PropertyToID("_GrainTemporalJitter");
        private static readonly int VintageStrength = Shader.PropertyToID("_VintageStrength");
        private static readonly int PosterizeLevels = Shader.PropertyToID("_PosterizeLevels");
        private static readonly int PosterizeMix = Shader.PropertyToID("_PosterizeMix");
        private static readonly int ShadowTintToGreen = Shader.PropertyToID("_ShadowTintToGreen");
        private static readonly int HighlightWarmth = Shader.PropertyToID("_HighlightWarmth");
        private static readonly int VignetteStrength = Shader.PropertyToID("_VignetteStrength");
        private static readonly int BrushStrength = Shader.PropertyToID("_BrushStrength");
        private static readonly int BrushScale = Shader.PropertyToID("_BrushScale");
        private static readonly int BrushAngleJitter = Shader.PropertyToID("_BrushAngleJitter");
        private static readonly int ColorMergeStrength = Shader.PropertyToID("_ColorMergeStrength");
        private static readonly int ColorMergeLevels = Shader.PropertyToID("_ColorMergeLevels");
        private static readonly int ColorMergeThreshold = Shader.PropertyToID("_ColorMergeThreshold");
        private static readonly int ColorMergeRadius = Shader.PropertyToID("_ColorMergeRadius");
        private static readonly int ColorMergeEdgeProtect = Shader.PropertyToID("_ColorMergeEdgeProtect");
        private static readonly int EffectBlend = Shader.PropertyToID("_EffectBlend");

        [SerializeField] private StylizeTarget stylizeTarget = StylizeTarget.GaussianOnly;
        [SerializeField] private RenderPassEvent stylizeEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        [SerializeField] private Shader stylizeShader;

        private Material _stylizeMaterial;
        private bool _loggedMissingStylizeShader;

        private class GsRenderPass : ScriptableRenderPass
        {
            private const string GaussianSplatRTName = "_GaussianSplatRT";
            private const string GaussianStylizeRTName = "_GaussianStylizedRT";

            private const string ProfilerTag = "GaussianSplatRenderGraph";
            private static readonly ProfilingSampler ProfileSampler = new(ProfilerTag);
            private static readonly int GaussianSplatRT = Shader.PropertyToID(GaussianSplatRTName);

            private readonly GaussianSplatURPFeature _owner;

            private class PassData
            {
                internal UniversalCameraData CameraData;
                internal TextureHandle SourceTexture;
                internal TextureHandle SourceDepth;
                internal TextureHandle GaussianSplatRT;
                internal TextureHandle StylizedGaussianRT;
                internal bool ApplyStylize;
                internal StylizeSettings StylizeSettings;
                internal Material StylizeMaterial;
                internal Vector2Int RenderSize;
            }

            public GsRenderPass(GaussianSplatURPFeature owner)
            {
                _owner = owner;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                var cameraData = frameData.Get<UniversalCameraData>();
                var resourceData = frameData.Get<UniversalResourceData>();

                using var builder = renderGraph.AddUnsafePass(ProfilerTag, out PassData passData);

                RenderTextureDescriptor rtDesc = cameraData.cameraTargetDescriptor;
                rtDesc.depthBufferBits = 0;
                rtDesc.msaaSamples = 1;
                rtDesc.graphicsFormat = GraphicsFormat.R16G16B16A16_SFloat;
                rtDesc.enableRandomWrite = true;  // allow compute shader UAV writes (tile renderer)
                TextureHandle textureHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianSplatRTName, true);
                bool applyStylize =
                    _owner.CanRunStylizeForCamera(cameraData.camera, StylizeTarget.GaussianOnly, out var stylizeSettings);
                TextureHandle stylizedHandle = textureHandle;
                if (applyStylize)
                    stylizedHandle = UniversalRenderer.CreateRenderGraphTexture(renderGraph, rtDesc, GaussianStylizeRTName, true);

                passData.RenderSize = new Vector2Int(rtDesc.width, rtDesc.height);
                passData.CameraData = cameraData;
                passData.SourceTexture = resourceData.activeColorTexture;
                passData.SourceDepth = resourceData.activeDepthTexture;
                passData.GaussianSplatRT = textureHandle;
                passData.StylizedGaussianRT = stylizedHandle;
                passData.ApplyStylize = applyStylize;
                passData.StylizeSettings = stylizeSettings;
                passData.StylizeMaterial = _owner._stylizeMaterial;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.activeDepthTexture);
                builder.UseTexture(textureHandle, AccessFlags.ReadWrite);
                if (applyStylize)
                    builder.UseTexture(stylizedHandle, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    var commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, ProfileSampler);
                    commandBuffer.SetGlobalTexture(GaussianSplatRT, data.GaussianSplatRT);
                    CoreUtils.SetRenderTarget(commandBuffer, data.GaussianSplatRT, data.SourceDepth, ClearFlag.Color, Color.clear);
                    // Pass GaussianSplatRT as the tile render output target so the tile
                    // renderer can write directly via UAV without SetRenderTarget/Blit.
                    GaussianSplatRenderSystem.instance.TileOutputTarget = data.GaussianSplatRT;
                    GaussianSplatRenderSystem.instance.TileRenderSize = data.RenderSize;
                    Material matComposite = GaussianSplatRenderSystem.instance.SortAndRenderSplats(data.CameraData.camera, commandBuffer);
                    if (matComposite != null)
                    {
                        TextureHandle composeSource = data.GaussianSplatRT;
                        if (data.ApplyStylize && data.StylizeMaterial != null)
                        {
                            SetStylizeMaterialProperties(data.StylizeMaterial, in data.StylizeSettings);
                            commandBuffer.SetGlobalTexture(StylizeSourceTex, data.GaussianSplatRT);
                            Blitter.BlitCameraTexture(commandBuffer, data.GaussianSplatRT, data.StylizedGaussianRT, data.StylizeMaterial, 0);
                            composeSource = data.StylizedGaussianRT;
                        }

                        commandBuffer.SetGlobalTexture(GaussianSplatRT, composeSource);
                        commandBuffer.BeginSample(GaussianSplatRenderSystem.ProfCompose);
                        Blitter.BlitCameraTexture(commandBuffer, composeSource, data.SourceTexture, matComposite, 0);
                        commandBuffer.EndSample(GaussianSplatRenderSystem.ProfCompose);
                    }
                });
            }
        }

        private class FullscreenStylizePass : ScriptableRenderPass
        {
            private const string FullscreenStylizeRTName = "_GaussianFullscreenStylizeRT";
            private const string ProfilerTag = "GaussianStylizeFullscreen";
            private static readonly ProfilingSampler ProfileSampler = new(ProfilerTag);

            private readonly GaussianSplatURPFeature _owner;

            private class PassData
            {
                internal TextureHandle SourceTexture;
                internal TextureHandle TempTexture;
                internal StylizeSettings StylizeSettings;
                internal Material StylizeMaterial;
            }

            public FullscreenStylizePass(GaussianSplatURPFeature owner)
            {
                _owner = owner;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                if (!_owner.CanRunStylizeForCamera(cameraData.camera, StylizeTarget.Fullscreen, out var stylizeSettings))
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
                passData.StylizeMaterial = _owner._stylizeMaterial;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(tempTexture, AccessFlags.ReadWrite);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData data, UnsafeGraphContext context) =>
                {
                    if (data.StylizeMaterial == null)
                        return;

                    CommandBuffer commandBuffer = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    using var _ = new ProfilingScope(commandBuffer, ProfileSampler);
                    SetStylizeMaterialProperties(data.StylizeMaterial, in data.StylizeSettings);
                    commandBuffer.SetGlobalTexture(StylizeSourceTex, data.SourceTexture);
                    Blitter.BlitCameraTexture(commandBuffer, data.SourceTexture, data.TempTexture, data.StylizeMaterial, 0);
                    commandBuffer.SetGlobalTexture(StylizeSourceTex, data.TempTexture);
                    Blitter.BlitCameraTexture(commandBuffer, data.TempTexture, data.SourceTexture, data.StylizeMaterial, 1);
                });
            }
        }

        private GsRenderPass _pass;
        private FullscreenStylizePass _fullscreenStylizePass;
        private bool _hasCamera;

        public override void Create()
        {
            EnsureStylizeMaterial();

            _pass = new GsRenderPass(this)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingTransparents
            };
            _fullscreenStylizePass = new FullscreenStylizePass(this)
            {
                renderPassEvent = stylizeEvent
            };
        }

        public override void OnCameraPreCull(ScriptableRenderer renderer, in CameraData cameraData)
        {
            _hasCamera = false;
            if (!IsSupportedCamera(cameraData.camera))
                return;

            var system = GaussianSplatRenderSystem.instance;
            if (!system.GatherSplatsForCamera(cameraData.camera))
                return;

            _hasCamera = true;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            EnsureStylizeMaterial();
            if (_hasCamera)
                renderer.EnqueuePass(_pass);

            if (stylizeTarget == StylizeTarget.Fullscreen && IsSupportedCamera(renderingData.cameraData.camera))
            {
                _fullscreenStylizePass.renderPassEvent = stylizeEvent;
                renderer.EnqueuePass(_fullscreenStylizePass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            _pass = null;
            _fullscreenStylizePass = null;
            CoreUtils.Destroy(_stylizeMaterial);
            _stylizeMaterial = null;
        }

        private void EnsureStylizeMaterial()
        {
            if (_stylizeMaterial != null)
                return;

            if (stylizeShader == null)
                stylizeShader = Shader.Find(StylizeShaderName);
            if (stylizeShader == null)
            {
                if (!_loggedMissingStylizeShader)
                {
                    Debug.LogWarning(
                        "Gaussian Stylize shader not found. Assign a shader in GaussianSplatURPFeature or include Hidden/Gaussian Splatting/Stylize.");
                    _loggedMissingStylizeShader = true;
                }
                return;
            }

            _stylizeMaterial = CoreUtils.CreateEngineMaterial(stylizeShader);
            _loggedMissingStylizeShader = false;
        }

        private bool CanRunStylizeForCamera(Camera camera, StylizeTarget target, out StylizeSettings settings)
        {
            settings = default;
            if (stylizeTarget != target)
                return false;
            if (!IsSupportedCamera(camera))
                return false;
            if (_stylizeMaterial == null)
                return false;
            return TryGetStylizeSettings(out settings);
        }

        private bool TryGetStylizeSettings(out StylizeSettings settings)
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
                posterizeMix = stylizeVolume.posterizeMix.value,
                shadowTintToGreen = stylizeVolume.shadowTintToGreen.value,
                highlightWarmth = stylizeVolume.highlightWarmth.value,
                vignette = stylizeVolume.vignette.value,
                brushStrength = stylizeVolume.brushStrength.value,
                brushScale = stylizeVolume.brushScale.value,
                brushAngleJitter = stylizeVolume.brushAngleJitter.value,
                colorMergeStrength = stylizeVolume.colorMergeStrength.value,
                colorMergeLevels = stylizeVolume.colorMergeLevels.value,
                colorMergeThreshold = stylizeVolume.colorMergeThreshold.value,
                colorMergeRadius = stylizeVolume.colorMergeRadius.value,
                colorMergeEdgeProtect = stylizeVolume.colorMergeEdgeProtect.value,
                blend = stylizeVolume.blend.value
            };
            return true;
        }

        private static bool IsSupportedCamera(Camera camera)
        {
            if (camera == null)
                return false;
            return camera.cameraType != CameraType.Preview && camera.cameraType != CameraType.Reflection;
        }

        private static void SetStylizeMaterialProperties(Material material, in StylizeSettings settings)
        {
            material.SetFloat(GrainIntensity, settings.grainIntensity);
            material.SetFloat(GrainScale, settings.grainScale);
            material.SetFloat(GrainTemporalJitter, settings.grainTemporalJitter);
            material.SetFloat(VintageStrength, settings.vintageStrength);
            material.SetFloat(PosterizeLevels, settings.posterizeLevels);
            material.SetFloat(PosterizeMix, settings.posterizeMix);
            material.SetFloat(ShadowTintToGreen, settings.shadowTintToGreen);
            material.SetFloat(HighlightWarmth, settings.highlightWarmth);
            material.SetFloat(VignetteStrength, settings.vignette);
            material.SetFloat(BrushStrength, settings.brushStrength);
            material.SetFloat(BrushScale, settings.brushScale);
            material.SetFloat(BrushAngleJitter, settings.brushAngleJitter);
            material.SetFloat(ColorMergeStrength, settings.colorMergeStrength);
            material.SetFloat(ColorMergeLevels, settings.colorMergeLevels);
            material.SetFloat(ColorMergeThreshold, settings.colorMergeThreshold);
            material.SetFloat(ColorMergeRadius, settings.colorMergeRadius);
            material.SetFloat(ColorMergeEdgeProtect, settings.colorMergeEdgeProtect);
            material.SetFloat(EffectBlend, settings.blend);
        }
    }
}

#endif // #if GS_ENABLE_URP
