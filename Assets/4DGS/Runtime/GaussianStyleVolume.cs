// SPDX-License-Identifier: MIT

using UnityEngine;

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
    public class GaussianStyleVolume : GaussianVolume, IGaussianTimelineTarget
    {
        private static readonly string[] ChannelNames = { "Weight", "Falloff" };

        [Tooltip("Style applied inside the volume")]
        public GaussianSplatStyle style;
        [Range(0f, 1f)]
        [Tooltip("Style weight inside the volume, before the styler's Weight scales it")]
        public float weight = 1f;

        protected override Color GizmoColor => Color.magenta;

        int IGaussianTimelineTarget.ChannelCount => ChannelNames.Length;

        string IGaussianTimelineTarget.GetChannelName(int index) => ChannelNames[index];

        float IGaussianTimelineTarget.GetChannelValue(int index) => index == 0 ? weight : falloff;

        void IGaussianTimelineTarget.SetChannelValue(int index, float value)
        {
            if (index == 0)
                weight = Mathf.Clamp01(value);
            else
                falloff = Mathf.Max(0f, value);
        }

        float IGaussianTimelineTarget.BlendChannel(int index, float from, float to, float t) => Mathf.Lerp(from, to, t);
    }
}
