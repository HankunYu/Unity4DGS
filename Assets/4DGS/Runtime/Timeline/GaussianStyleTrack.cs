using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Switches and fades a renderer's style. A renderer wears one style at a
    /// time: the heaviest clip picks it, and the weights of clips wearing that
    /// style add up, so two of them crossfade. When the picked style is the
    /// renderer's own, its own weight fills in what the clips leave.
    /// </summary>
    [Serializable]
    public class GaussianStyleTrack : GaussianTimelineClipTrack
    {
        public override Type BindingType => typeof(GaussianSplatRenderer);

        public override Type ClipType => typeof(GaussianStyleClip);

        public override GaussianTimelineTrackInstance CreateInstance(GaussianTimelinePlayer player, Object binding)
        {
            return new Instance(this, (GaussianSplatRenderer)binding);
        }

        private sealed class Instance : GaussianTimelineTrackInstance
        {
            private readonly GaussianStyleTrack _track;
            private readonly GaussianSplatRenderer _renderer;
            private readonly GaussianSplatStyle _ownStyle;
            private readonly float _ownWeight;
            private readonly List<GaussianClipSample> _samples = new();
            private bool _driving;

            public Instance(GaussianStyleTrack track, GaussianSplatRenderer renderer)
            {
                _track = track;
                _renderer = renderer;
                _ownStyle = renderer.style;
                _ownWeight = renderer.styleWeight;
            }

            public override void Evaluate(in GaussianTimelineEvaluation evaluation)
            {
                if (_renderer == null)
                    return;
                _track.Gather(evaluation, _samples);
                int strongest = GaussianTimelineClipTrack.Strongest(_samples);
                if (strongest < 0)
                {
                    if (_driving)
                        Restore();
                    _driving = false;
                    return;
                }

                GaussianSplatStyle style = Clip(_samples[strongest]).Style;
                float weight = 0f;
                float total = 0f;
                foreach (GaussianClipSample sample in _samples)
                {
                    total += sample.Weight;
                    GaussianStyleClip clip = Clip(sample);
                    if (clip.Style == style)
                        weight += sample.Weight * clip.Weight;
                }
                if (style == _ownStyle && total < 1f)
                    weight += (1f - total) * _ownWeight;

                _renderer.style = style;
                _renderer.styleWeight = Mathf.Clamp01(weight);
                _driving = true;
            }

            public override void Restore()
            {
                if (_renderer == null)
                    return;
                _renderer.style = _ownStyle;
                _renderer.styleWeight = _ownWeight;
            }

            private GaussianStyleClip Clip(GaussianClipSample sample)
            {
                return (GaussianStyleClip)_track.Clips[sample.ClipIndex];
            }
        }
    }
}
