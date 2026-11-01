using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>
    /// Named points in time. Playback that passes one raises the player's
    /// OnMarker and, when bound, the receiver's events for that name. Seeking
    /// and scrubbing past a marker fire nothing.
    /// </summary>
    [Serializable]
    [TideDisplay("Marker", 0.85f, 0.85f, 0.85f)]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianMarkerTrack")]
    public class TideMarkerTrack : TideTrack
    {
        [SerializeField] private List<TideMarker> markers = new();

        public List<TideMarker> Markers => markers;

        public override Type BindingType => typeof(TideMarkerReceiver);

        public override bool BindingOptional => true;

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            return new Instance(this, player, binding as TideMarkerReceiver);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly TideMarkerTrack _track;
            private readonly TidePlayer _player;
            private readonly TideMarkerReceiver _receiver;

            public Instance(TideMarkerTrack track, TidePlayer player, TideMarkerReceiver receiver)
            {
                _track = track;
                _player = player;
                _receiver = receiver;
            }

            public override void Evaluate(in TideEvaluation evaluation)
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
                foreach (TideMarker marker in _track.markers)
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
