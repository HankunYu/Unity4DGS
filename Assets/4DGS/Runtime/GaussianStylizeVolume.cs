// SPDX-License-Identifier: MIT
#if GS_ENABLE_URP

using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    [VolumeComponentMenu("Gaussian Splatting/Stylize")]
    public sealed class GaussianStylizeVolume : VolumeComponent
    {
        public BoolParameter enable = new(false);

        public ClampedFloatParameter grainIntensity = new(0.25f, 0f, 1f);
        public ClampedFloatParameter grainScale = new(1.2f, 0.5f, 4f);
        public ClampedFloatParameter grainTemporalJitter = new(0.2f, 0f, 1f);

        public ClampedFloatParameter vintageStrength = new(0.55f, 0f, 1f);
        public ClampedFloatParameter posterizeLevels = new(6f, 2f, 16f);
        public ClampedFloatParameter posterizeMix = new(0.5f, 0f, 1f);
        public ClampedFloatParameter shadowTintToGreen = new(0.35f, 0f, 1f);
        public ClampedFloatParameter highlightWarmth = new(0.25f, 0f, 1f);
        public ClampedFloatParameter vignette = new(0.1f, 0f, 1f);
        public ClampedFloatParameter brushStrength = new(0.75f, 0f, 1f);
        public ClampedFloatParameter brushScale = new(1.8f, 0.5f, 4f);
        public ClampedFloatParameter brushAngleJitter = new(0.2f, 0f, 1f);
        public ClampedFloatParameter colorMergeStrength = new(0f, 0f, 1f);
        public ClampedFloatParameter colorMergeLevels = new(5f, 2f, 16f);
        public ClampedFloatParameter colorMergeThreshold = new(0.35f, 0f, 1f);
        public ClampedFloatParameter colorMergeRadius = new(0.5f, 0f, 1f);
        public ClampedFloatParameter colorMergeEdgeProtect = new(0.75f, 0f, 1f);

        public ClampedFloatParameter blend = new(1f, 0f, 1f);

        public bool IsActive()
        {
            if (!enable.value || blend.value <= 0f)
                return false;

            return grainIntensity.value > 0f
                || vintageStrength.value > 0f
                || (posterizeLevels.value < 16f && posterizeMix.value > 0f)
                || vignette.value > 0f
                || brushStrength.value > 0f
                || colorMergeStrength.value > 0f;
        }
    }
}

#endif // #if GS_ENABLE_URP
