// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    // The styles a renderer draws with and the regions they apply in, as its
    // GaussianStyler set them up, packed for the shaders. Each distinct style
    // takes a slot; each volume names its style's slot. The view-data pass
    // finds a splat's strongest volume (StyleAt in GaussianStyle.hlsl) and
    // reads that slot's parameters.
    internal sealed class GaussianStyleSet
    {
        // Must match MAX_STYLES and MAX_STYLE_VOLUMES in GaussianStyle.hlsl;
        // MaxStyles also fits the two slot bits of the baked view color
        public const int MaxStyles = 4;
        public const int MaxVolumes = 8;

        private static class Props
        {
            public static readonly int StyleWeight = Shader.PropertyToID("_StyleWeight");
            public static readonly int StyleTime = Shader.PropertyToID("_StyleTime");
            public static readonly int StyleVolumeCount = Shader.PropertyToID("_StyleVolumeCount");
            public static readonly int StyleVolumeWorldToLocal = Shader.PropertyToID("_StyleVolumeWorldToLocal");
            public static readonly int StyleVolumeParams = Shader.PropertyToID("_StyleVolumeParams");
            public static readonly int StyleRampTex = Shader.PropertyToID("_StyleRampTex");
            public static readonly int StyleColorParams = Shader.PropertyToID("_StyleColorParams");
            public static readonly int StyleLightParams = Shader.PropertyToID("_StyleLightParams");
            public static readonly int StyleGlintParams = Shader.PropertyToID("_StyleGlintParams");
            public static readonly int StyleShapeParams = Shader.PropertyToID("_StyleShapeParams");
            public static readonly int StyleStreakParams = Shader.PropertyToID("_StyleStreakParams");
            public static readonly int StyleSizeParams = Shader.PropertyToID("_StyleSizeParams");
            public static readonly int StyleQuantizeParams = Shader.PropertyToID("_StyleQuantizeParams");
            public static readonly int StyleSliceNormals = Shader.PropertyToID("_StyleSliceNormals");
            public static readonly int StyleReach = Shader.PropertyToID("_StyleReach");
            public static readonly int StyleChunkExpand = Shader.PropertyToID("_StyleChunkExpand");
        }

        private readonly GaussianSplatStyle[] _styles = new GaussianSplatStyle[MaxStyles];
        private readonly Matrix4x4[] _volumeWorldToLocal = new Matrix4x4[MaxVolumes];
        private readonly Vector4[] _volumeParams = new Vector4[MaxVolumes];
        private readonly Vector4[] _slotParams0 = new Vector4[MaxStyles];
        private readonly Vector4[] _slotParams1 = new Vector4[MaxStyles];
        private readonly Vector4[] _slotParams2 = new Vector4[MaxStyles];
        private readonly Vector4[] _slotParams3 = new Vector4[MaxStyles];
        private readonly Vector4[] _slotParams4 = new Vector4[MaxStyles];
        // One row per slot, copied from each style's own ramp
        private Texture2D _ramps;
        // Some volume styles every splat at full weight
        private bool _coversAll;

        public int StyleCount { get; private set; }
        public int VolumeCount { get; private set; }
        public float Weight { get; private set; }

        public bool Active => Weight > 0f && VolumeCount > 0;

        // Every splat fully styled, by styles that only add light: they blend
        // the same in any order, so the renderer skips the sort
        public bool OrderIndependent
        {
            get
            {
                if (!Active || Weight < 1f || !_coversAll)
                    return false;
                for (int i = 0; i < StyleCount; i++)
                {
                    if (_styles[i].Additivity < 1f)
                        return false;
                }
                return true;
            }
        }

        public void Begin(float weight)
        {
            Array.Clear(_styles, 0, _styles.Length);
            StyleCount = 0;
            VolumeCount = 0;
            _coversAll = false;
            Weight = Mathf.Clamp01(weight);
        }

        // False when the volume has nothing to apply or the set is full
        public bool AddVolume(GaussianStyleVolume volume)
        {
            if (volume.style == null || volume.weight <= 0f || VolumeCount >= MaxVolumes)
                return false;
            int slot = Array.IndexOf(_styles, volume.style, 0, StyleCount);
            if (slot < 0)
            {
                if (StyleCount >= MaxStyles)
                    return false;
                slot = StyleCount++;
                _styles[slot] = volume.style;
            }
            _volumeWorldToLocal[VolumeCount] = volume.transform.worldToLocalMatrix;
            _volumeParams[VolumeCount] = new Vector4((float)volume.shape, volume.falloff, volume.weight, slot);
            VolumeCount++;
            if (volume.shape == GaussianVolumeShape.Unbounded && volume.weight >= 1f)
                _coversAll = true;
            return true;
        }

        // How far what the styles draw can lie from a splat's own position,
        // in world units (GaussianSplatStyle.Reach), at the set's weight
        public float Reach(float maxSpeed)
        {
            float reach = 0f;
            for (int i = 0; i < StyleCount; i++)
                reach = Mathf.Max(reach, _styles[i].Reach(maxSpeed));
            return reach * Weight;
        }

        // The draw-side parameters of each slot, for the quad shader
        public GaussianSplatStyleRange StyleRange(int start, bool baked)
        {
            var range = new GaussianSplatStyleRange { start = (uint)start, baked = baked ? 1u : 0u };
            if (!Active)
                return range;
            for (int i = 0; i < StyleCount; i++)
            {
                range.additivity[i] = _styles[i].Additivity;
                range.shape[i] = (float)_styles[i].Shape;
                range.streakTailPower[i] = _styles[i].StreakTailPower;
            }
            return range;
        }

        // The color side (GaussianStyle.hlsl): for the view data, and for any
        // other compute shader that colors splats the way the renderer draws
        // them, e.g. GaussianParticleEmitter. Disabled, it binds no style.
        public void SetColorOnCS(CommandBuffer cmb, ComputeShader cs, int kernelIndex, bool enabled)
        {
            bool active = enabled && Active;
            cmb.SetComputeFloatParam(cs, Props.StyleWeight, active ? Weight : 0f);
            cmb.SetComputeIntParam(cs, Props.StyleVolumeCount, active ? VolumeCount : 0);
            cmb.SetComputeMatrixArrayParam(cs, Props.StyleVolumeWorldToLocal, _volumeWorldToLocal);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleVolumeParams, _volumeParams);
            cmb.SetComputeFloatParam(cs, Props.StyleTime, StyleClock);
            // The kernel samples the ramps whether or not a style is set
            if (!active)
            {
                cmb.SetComputeTextureParam(cs, kernelIndex, Props.StyleRampTex, Texture2D.whiteTexture);
                return;
            }

            EnsureRamps();
            for (int i = 0; i < StyleCount; i++)
            {
                GaussianSplatStyle style = _styles[i];
                cmb.CopyTexture(style.RampTexture, 0, 0, 0, 0, GaussianSplatStyle.RampResolution, 1, _ramps, 0, 0, 0, i);
                _slotParams0[i] = new Vector4((float)style.ColorSource, (float)style.RampInput, style.RampDepthNear,
                    1f / Mathf.Max(style.RampDepthFar - style.RampDepthNear, 1e-4f));
                _slotParams1[i] = new Vector4(style.Intensity, style.RimAmount, style.RimPower, 0f);
                _slotParams2[i] = new Vector4(style.GlintDensity, style.GlintRate, style.GlintIntensity, 0f);
            }
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.StyleRampTex, _ramps);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleColorParams, _slotParams0);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleLightParams, _slotParams1);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleGlintParams, _slotParams2);
        }

        // The shape, snap and blend side (SplatUtilities.compute). The reach
        // margins are read whether or not a style is set; objectScale is the
        // renderer's, for the object-space chunk bounds. Disabled, it binds no
        // style.
        public void SetShapeOnCS(CommandBuffer cmb, ComputeShader cs, bool enabled, float maxSpeed, Vector3 objectScale)
        {
            bool active = enabled && Active;
            for (int i = 0; i < StyleCount; i++)
            {
                GaussianSplatStyle style = _styles[i];
                bool quantized = style.Quantize != GaussianSplatStyleQuantize.None;
                _slotParams0[i] = new Vector4((float)style.Shape, style.DotSize * 0.5f, (float)style.StreakAxis, style.StreakLength);
                Vector3 dir = style.StreakDirection;
                _slotParams1[i] = new Vector4(dir.x, dir.y, dir.z, style.StreakTime);
                _slotParams2[i] = new Vector4(style.StreakWidth * 0.5f, style.MinWorldSize, style.Additivity, style.KeepFraction);
                _slotParams3[i] = new Vector4((float)style.Quantize, style.QuantizeSpacing,
                    quantized ? style.QuantizeBand * style.QuantizeSpacing * 0.5f : float.MaxValue, style.SliceOffset);
                Vector3 normal = style.SliceNormal;
                _slotParams4[i] = new Vector4(normal.x, normal.y, normal.z, 0f);
            }
            // Empty slots stay plain and additive-free, for occlusion culling
            for (int i = StyleCount; i < MaxStyles; i++)
            {
                _slotParams0[i] = Vector4.zero;
                _slotParams2[i] = Vector4.zero;
                _slotParams3[i] = new Vector4(0f, 1f, float.MaxValue, 0f);
            }
            cmb.SetComputeVectorArrayParam(cs, Props.StyleShapeParams, _slotParams0);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleStreakParams, _slotParams1);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleSizeParams, _slotParams2);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleQuantizeParams, _slotParams3);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleSliceNormals, _slotParams4);

            // The reach shrinks with the weight, like the snap and shape it bounds
            float reach = active ? Reach(maxSpeed) : 0f;
            cmb.SetComputeFloatParam(cs, Props.StyleReach, reach);
            // Object-space reach for the chunk bounds; the smallest axis scale
            // stretches a world distance the most
            float minScale = Mathf.Max(Mathf.Min(Mathf.Abs(objectScale.x), Mathf.Min(Mathf.Abs(objectScale.y), Mathf.Abs(objectScale.z))), 1e-6f);
            cmb.SetComputeFloatParam(cs, Props.StyleChunkExpand, reach / minScale);
        }

        public void Dispose()
        {
            if (_ramps != null)
                Object.DestroyImmediate(_ramps);
            _ramps = null;
        }

        private void EnsureRamps()
        {
            if (_ramps != null)
                return;
            // Linear, like each style's own ramp: splat colors are gamma space
            // at this stage and the values go in unconverted
            _ramps = new Texture2D(GaussianSplatStyle.RampResolution, MaxStyles, TextureFormat.RGBAHalf, false, true)
            {
                name = "GaussianStyleRamps",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave,
            };
            // Upload once without a CPU copy; the rows arrive by CopyTexture
            _ramps.Apply(false, true);
        }

        // Same clock as GaussianAnimator, so a capture under
        // Time.captureFramerate is deterministic and edit mode still moves
        private static float StyleClock
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    return (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif
                return Time.time;
            }
        }
    }
}
