using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>
    /// Nests another player's timeline, e.g. one shot inside the film's
    /// master timeline. The child player keeps its own bindings; while this
    /// track is bound it stops its own clock and shows the time the heaviest
    /// clip maps to. Between clips its targets get their own values back.
    /// </summary>
    [Serializable]
    [TideDisplay("Sub Timeline", 0.58f, 0.42f, 0.78f)]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianSubTimelineTrack")]
    public class TideSubTimelineTrack : TideClipTrack
    {
        public override Type BindingType => typeof(TidePlayer);

        public override Type ClipType => typeof(TideSubTimelineClip);

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            var child = (TidePlayer)binding;
            if (!child.BeginDriven(player))
            {
                Debug.LogWarning($"Timeline track '{Name}': {child.name} is this player, nests it or is already driven", child);
                return null;
            }
            return new Instance(this, player, child);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly TideSubTimelineTrack _track;
            private readonly TidePlayer _player;
            private readonly TidePlayer _child;
            private readonly List<TideClipSample> _samples = new();
            private int _lastClip = -1;
            private double _lastChildTime;

            public Instance(TideSubTimelineTrack track, TidePlayer player, TidePlayer child)
            {
                _track = track;
                _player = player;
                _child = child;
            }

            public override void Evaluate(in TideEvaluation evaluation)
            {
                if (_child == null)
                    return;
                _track.Gather(evaluation, _samples);
                int strongest = TideClipTrack.Strongest(_samples);
                if (strongest < 0)
                {
                    if (_lastClip >= 0)
                        _child.ReleaseDriven();
                    _lastClip = -1;
                    return;
                }

                TideClipSample sample = _samples[strongest];
                double childTime = ((TideSubTimelineClip)_track.Clips[sample.ClipIndex]).SubTime(sample.LocalTime);
                // Played on from the same clip: the child's markers and audio carry on too
                bool continuous = evaluation.Continuous && sample.ClipIndex == _lastClip && childTime >= _lastChildTime;
                _child.EvaluateDriven(childTime, continuous ? _lastChildTime : childTime, continuous);
                _lastClip = sample.ClipIndex;
                _lastChildTime = childTime;
            }

            public override void Restore()
            {
                if (_child != null)
                    _child.EndDriven(_player);
            }
        }
    }
}
