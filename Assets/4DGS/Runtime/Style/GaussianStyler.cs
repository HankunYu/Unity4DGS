// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Styles a GaussianSplatRenderer, the way GaussianAnimator animates it:
    /// each GaussianStyleVolume under it applies its own style within its
    /// region, and where volumes overlap the strongest one decides. An
    /// Unbounded volume styles the whole renderer; without volumes nothing is
    /// styled. Weight scales every volume at once. Timelines drive it, and
    /// each volume's Weight and Falloff, as channels to fade and switch styles.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Style/Styler")]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [DisallowMultipleComponent]
    public class GaussianStyler : MonoBehaviour, ITideChannelTarget
    {
        private static readonly string[] ChannelNames = { "Weight" };

        [Range(0f, 1f)]
        [Tooltip("Scales the weight of every style volume; animate it to fade all styles in or out together")]
        public float weight = 1f;

        private readonly List<GaussianStyleVolume> _volumes = new();

        int ITideChannelTarget.ChannelCount => ChannelNames.Length;

        string ITideChannelTarget.GetChannelName(int index) => ChannelNames[index];

        TideValueType ITideChannelTarget.GetChannelType(int index) => TideValueType.Float;

        TideValue ITideChannelTarget.GetChannelValue(int index) => weight;

        void ITideChannelTarget.SetChannelValue(int index, TideValue value) => weight = Mathf.Clamp01(value.AsFloat);

        // Loads the active volumes under this styler and their styles, as
        // many as the shaders take (GaussianStyleSet.MaxStyles/MaxVolumes)
        internal void FillStyleSet(GaussianStyleSet set)
        {
            set.Begin(isActiveAndEnabled ? weight : 0f);
            GaussianHierarchy.GetOwnedComponents(this, _volumes);
            foreach (GaussianStyleVolume volume in _volumes)
            {
                if (volume.isActiveAndEnabled)
                    set.AddVolume(volume);
            }
        }
    }
}
