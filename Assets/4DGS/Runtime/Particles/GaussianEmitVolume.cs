// SPDX-License-Identifier: MIT

using UnityEngine;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Shapes where a GaussianParticleEmitter's particles come from. Placed
    /// under the emitter, a Birth volume limits births to the source splats
    /// inside it (e.g. only the hands), and a Release volume frees attached
    /// particles as it reaches them; sweep one across the body on a timeline
    /// to have it break into particles. Timelines drive Weight and Falloff as
    /// channels.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Particles/Emit Volume")]
    public class GaussianEmitVolume : GaussianVolume, ITideChannelTarget
    {
        private static readonly string[] ChannelNames = { "Weight", "Falloff" };

        public GaussianEmitVolumeRole role = GaussianEmitVolumeRole.Birth;
        [Range(0f, 1f)]
        [Tooltip("Birth: chance a particle born inside is shown. Release: an attached particle inside lets go once this weight where it is passes its own random threshold, so a soft edge frees particles gradually")]
        public float weight = 1f;

        protected override Color GizmoColor => role == GaussianEmitVolumeRole.Birth ? new Color(1f, 0.6f, 0.1f) : new Color(0.2f, 0.8f, 1f);

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
