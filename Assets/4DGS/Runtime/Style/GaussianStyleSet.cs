// SPDX-License-Identifier: MIT

using System;
using UnityEngine;
using UnityEngine.Rendering;
using Tide;

namespace GaussianSplatting.Runtime
{
    // The styles a renderer draws with and the regions they apply in, as its
    // GaussianStyler set them up, packed for the shaders. Each distinct style
    // takes a slot; each volume names its style's slot. The view-data pass
    // finds a splat's strongest volume (StyleAt in GaussianStyle.hlsl) and
    // reads that slot's blocks, one channel per kind of block.
    internal sealed class GaussianStyleSet
    {
        // Must match MAX_STYLES and MAX_STYLE_VOLUMES in GaussianStyleCore.hlsl;
        // MaxStyles also fits the two slot bits of the baked view color
        public const int MaxStyles = 4;
        public const int MaxVolumes = 8;

        private static class Props
        {
            public static readonly int StyleWeight = Shader.PropertyToID("_StyleWeight");
            public static readonly int StyleTime = Shader.PropertyToID("_StyleTime");
            public static readonly int StyleVolumeCount = Shader.PropertyToID("_StyleVolumeCount");
            public static readonly int StyleVolumeWorldToLocal = Shader.PropertyToID("_StyleVolumeWorldToLocal");
            public static readonly int StyleVolume = Shader.PropertyToID("_StyleVolume");
            public static readonly int StyleReach = Shader.PropertyToID("_StyleReach");
            public static readonly int StyleChunkExpand = Shader.PropertyToID("_StyleChunkExpand");
        }

        private readonly GaussianSplatStyle[] _styles = new GaussianSplatStyle[MaxStyles];
        // Each volume as LoadStyleVolume (GaussianStyleCore.hlsl) unpacks it
        private const int VolumeRows = 2;
        private readonly Matrix4x4[] _volumeWorldToLocal = new Matrix4x4[MaxVolumes];
        private readonly Vector4[] _volumeRows = new Vector4[MaxVolumes * VolumeRows];
        private readonly GaussianStyleChannel[] _channels = GaussianStyleBlocks.CreateChannels();
        // The channels hold the styles as they are now
        private bool _packed;
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
                GaussianSplatStyleRange range = StyleRange(0, true);
                for (int i = 0; i < StyleCount; i++)
                {
                    if (range.additivity[i] < 1f)
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
            _packed = false;
            Weight = Mathf.Clamp01(weight);
        }

        // False when the volume has nothing to apply or the set is full
        public bool AddVolume(GaussianStyleVolume volume)
        {
            if (volume.style == null || volume.weight <= 0f || VolumeCount >= MaxVolumes)
                return false;
            // A region scaled down to nothing holds no splat; its matrix
            // would put every splat at its centre, inside it
            if (volume.shape != GaussianVolumeShape.Unbounded && volume.Collapsed)
                return false;
            int slot = Array.IndexOf(_styles, volume.style, 0, StyleCount);
            if (slot < 0)
            {
                if (StyleCount >= MaxStyles)
                    return false;
                slot = StyleCount++;
                _styles[slot] = volume.style;
            }
            Vector3 center = volume.transform.position;
            _volumeWorldToLocal[VolumeCount] = volume.transform.worldToLocalMatrix;
            _volumeRows[VolumeCount * VolumeRows] = new Vector4(center.x, center.y, center.z, slot);
            _volumeRows[VolumeCount * VolumeRows + 1] = new Vector4((float)volume.shape, volume.falloff, volume.weight, volume.scatter);
            VolumeCount++;
            if (volume.shape == GaussianVolumeShape.Unbounded && volume.weight >= 1f)
                _coversAll = true;
            return true;
        }

        // How far what the styles draw can lie from a splat's own position,
        // in world units (GaussianSplatStyle.Reach), at the set's weight
        public float Reach(float maxSpeed, float maxSegment)
        {
            float reach = 0f;
            for (int i = 0; i < StyleCount; i++)
                reach = Mathf.Max(reach, _styles[i].Reach(maxSpeed, maxSegment));
            return reach * Weight;
        }

        // The draw-side parameters of each slot, for the quad shader
        public GaussianSplatStyleRange StyleRange(int start, bool baked)
        {
            var range = new GaussianSplatStyleRange { start = (uint)start, baked = baked ? 1u : 0u };
            if (!Active)
                return range;
            for (int i = 0; i < StyleCount; i++)
                _styles[i].FillRange(ref range, i);
            return range;
        }

        // The styles and their volumes (GaussianStyle.hlsl): for the view
        // data, and for any other compute shader that colors splats the way
        // the renderer draws them, e.g. GaussianParticleEmitter. Disabled,
        // no style applies.
        public void SetOnCS(CommandBuffer cmb, ComputeShader cs, int kernelIndex, bool enabled)
        {
            bool active = enabled && Active;
            cmb.SetComputeFloatParam(cs, Props.StyleWeight, active ? Weight : 0f);
            cmb.SetComputeIntParam(cs, Props.StyleVolumeCount, active ? VolumeCount : 0);
            cmb.SetComputeFloatParam(cs, Props.StyleTime, StyleClock);

            cmb.SetComputeMatrixArrayParam(cs, Props.StyleVolumeWorldToLocal, _volumeWorldToLocal);
            cmb.SetComputeVectorArrayParam(cs, Props.StyleVolume, _volumeRows);

            if (!_packed)
            {
                foreach (GaussianStyleChannel channel in _channels)
                {
                    // Slots without a style read as absent: plain and
                    // additive-free, for occlusion culling
                    for (int i = 0; i < MaxStyles; i++)
                        channel.Write(i, _styles[i]);
                }
                _packed = true;
            }
            foreach (GaussianStyleChannel channel in _channels)
                channel.Bind(cmb, cs, kernelIndex, active);
        }

        // The culling margins (SplatUtilities.compute), read whether or not
        // a style is set; maxSpeed and maxSegment bound the renderer's
        // velocity and segment sources (0 without one), objectScale is the
        // renderer's, for the object-space chunk bounds
        public void SetReachOnCS(CommandBuffer cmb, ComputeShader cs, bool enabled, float maxSpeed, float maxSegment, Vector3 objectScale)
        {
            // The reach shrinks with the weight, like the snap and shape it bounds
            float reach = enabled && Active ? Reach(maxSpeed, maxSegment) : 0f;
            cmb.SetComputeFloatParam(cs, Props.StyleReach, reach);
            // Object-space reach for the chunk bounds; the smallest axis scale
            // stretches a world distance the most
            float minScale = Mathf.Max(Mathf.Min(Mathf.Abs(objectScale.x), Mathf.Min(Mathf.Abs(objectScale.y), Mathf.Abs(objectScale.z))), 1e-6f);
            cmb.SetComputeFloatParam(cs, Props.StyleChunkExpand, reach / minScale);
        }

        public void Dispose()
        {
            foreach (GaussianStyleChannel channel in _channels)
                channel.Dispose();
        }

        // Same clock as GaussianAnimator: the timeline's time while one
        // drives the scene, so captures repeat, and edit mode still moves
        private static float StyleClock => (float)TideClock.Time;
    }
}
