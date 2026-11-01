// SPDX-License-Identifier: MIT

using UnityEngine;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Applies a style within a region: placed under a GaussianStyler, it
    /// styles the splats inside it, at this volume's weight where the splat is
    /// times the styler's Weight. Where volumes overlap, the strongest decides
    /// a splat's style. Unbounded covers the whole renderer. To have the
    /// region follow the camera, give the volume a Parent Constraint with the
    /// camera as its source; animate its scale to sweep the style across the
    /// scene, or fade one volume out while another fades in to switch styles.
    /// Timelines drive Weight and Falloff as channels.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Style/Style Volume")]
    public class GaussianStyleVolume : GaussianVolume, ITideChannelTarget
    {
        private static readonly string[] ChannelNames = { "Weight", "Falloff" };

        [Tooltip("Style applied inside the volume")]
        public GaussianSplatStyle style;
        [Range(0f, 1f)]
        [Tooltip("Style weight inside the volume, before the styler's Weight scales it")]
        public float weight = 1f;

        protected override Color GizmoColor => Color.magenta;

        int ITideChannelTarget.ChannelCount => ChannelNames.Length;

        string ITideChannelTarget.GetChannelName(int index) => ChannelNames[index];

        TideValueType ITideChannelTarget.GetChannelType(int index) => TideValueType.Float;

        TideValue ITideChannelTarget.GetChannelValue(int index) => index == 0 ? weight : falloff;

        void ITideChannelTarget.SetChannelValue(int index, TideValue channelValue)
        {
            float value = channelValue.AsFloat;
            if (index == 0)
                weight = Mathf.Clamp01(value);
            else
                falloff = Mathf.Max(0f, value);
        }
    }
}
