using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Named points in time. Playback that passes one raises the player's
    /// OnMarker and, when bound, the receiver's events for that name. Seeking
    /// and scrubbing past a marker fire nothing.
    /// </summary>
    [Serializable]
    public class GaussianMarkerTrack : GaussianTimelineTrack
    {
        [SerializeField] private List<GaussianTimelineMarker> markers = new();

        public List<GaussianTimelineMarker> Markers => markers;

        public override Type BindingType => typeof(GaussianMarkerReceiver);

        public override bool BindingOptional => true;

        public override GaussianTimelineTrackInstance CreateInstance(GaussianTimelinePlayer player, Object binding)
        {
            return new Instance(this, player, binding as GaussianMarkerReceiver);
        }

        private sealed class Instance : GaussianTimelineTrackInstance
        {
            private readonly GaussianMarkerTrack _track;
            private readonly GaussianTimelinePlayer _player;
            private readonly GaussianMarkerReceiver _receiver;

            public Instance(GaussianMarkerTrack track, GaussianTimelinePlayer player, GaussianMarkerReceiver receiver)
            {
                _track = track;
                _player = player;
                _receiver = receiver;
            }

            public override void Evaluate(in GaussianTimelineEvaluation evaluation)
            {
                if (!evaluation.Continuous)
                    return;
                if (evaluation.Wrapped)
                {
                    FireBetween(evaluation.PreviousTime, evaluation.Duration, true);
                    FireBetween(0.0, evaluation.Time, false);
                }
                else
                {
                    // Playback holding on the last frame takes a marker at the very end
                    FireBetween(evaluation.PreviousTime, evaluation.Time, evaluation.Time >= evaluation.Duration);
                }
            }

            public override void Restore()
            {
            }

            // Markers in [from, to), or [from, to] when the end is included
            private void FireBetween(double from, double to, bool includeEnd)
            {
                foreach (GaussianTimelineMarker marker in _track.markers)
                {
                    if (marker.Time >= from && (marker.Time < to || includeEnd && marker.Time <= to))
                    {
                        _player.RaiseMarker(marker.Name);
                        if (_receiver != null)
                            _receiver.Receive(marker.Name);
                    }
                }
            }
        }
    }
}
