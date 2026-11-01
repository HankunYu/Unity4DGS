using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Shows a <see cref="GaussianPlayer"/>'s 4D sequence at the frame the
    /// timeline is on, so the capture stays in step with everything else.
    /// The heaviest clip picks the frame; between clips the player plays on
    /// by itself as it would without a timeline.
    /// </summary>
    [Serializable]
    [TideDisplay("Sequence", 0.42f, 0.42f, 0.80f)]
    public class GaussianSequenceTrack : TideClipTrack
    {
        public override Type BindingType => typeof(GaussianPlayer);

        public override Type ClipType => typeof(GaussianSequenceClip);

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            return new Instance(this, (GaussianPlayer)binding);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly GaussianSequenceTrack _track;
            private readonly GaussianPlayer _player;
            private readonly bool _ownPlaying;
            private readonly int _ownFrame;
            private readonly List<TideClipSample> _samples = new();
            private bool _driving;

            public Instance(GaussianSequenceTrack track, GaussianPlayer player)
            {
                _track = track;
                _player = player;
                _ownPlaying = player.isPlaying;
                _ownFrame = player.currentFrame;
            }

            public override void Evaluate(in TideEvaluation evaluation)
            {
                if (_player == null)
                    return;
                GaussianSplatSequence sequence = _player.sequence;
                _track.Gather(evaluation, _samples);
                int strongest = TideClipTrack.Strongest(_samples);
                if (strongest < 0 || sequence == null || sequence.FrameCount == 0)
                {
                    if (_driving)
                        _player.isPlaying = _ownPlaying;
                    _driving = false;
                    return;
                }

                TideClipSample sample = _samples[strongest];
                int frame = ((GaussianSequenceClip)_track.Clips[sample.ClipIndex]).FrameAt(sample.LocalTime, sequence);
                _player.isPlaying = false;
                if (frame != _player.currentFrame)
                    _player.ShowFrame(frame);
                _driving = true;
            }

            public override void Restore()
            {
                if (_player == null)
                    return;
                _player.isPlaying = _ownPlaying;
                _player.ShowFrame(_ownFrame);
            }
        }
    }
}
