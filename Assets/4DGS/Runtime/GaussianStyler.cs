// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using UnityEngine;

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
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [DisallowMultipleComponent]
    public class GaussianStyler : MonoBehaviour, IGaussianTimelineTarget
    {
        private static readonly string[] ChannelNames = { "Weight" };

        [Range(0f, 1f)]
        [Tooltip("Scales the weight of every style volume; animate it to fade all styles in or out together")]
        public float weight = 1f;

        private readonly List<GaussianStyleVolume> _volumes = new();

        int IGaussianTimelineTarget.ChannelCount => ChannelNames.Length;

        string IGaussianTimelineTarget.GetChannelName(int index) => ChannelNames[index];

        float IGaussianTimelineTarget.GetChannelValue(int index) => weight;

        void IGaussianTimelineTarget.SetChannelValue(int index, float value) => weight = Mathf.Clamp01(value);

        float IGaussianTimelineTarget.BlendChannel(int index, float from, float to, float t) => Mathf.Lerp(from, to, t);

        // Loads the active volumes under this styler and their styles, as
        // many as the shaders take (GaussianStyleSet.MaxStyles/MaxVolumes)
        internal void FillStyleSet(GaussianStyleSet set)
        {
            set.Begin(isActiveAndEnabled ? weight : 0f);
            GetComponentsInChildren(_volumes);
            foreach (GaussianStyleVolume volume in _volumes)
            {
                if (volume.isActiveAndEnabled)
                    set.AddVolume(volume);
            }
        }
    }
}
