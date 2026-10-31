using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Plays sounds on an AudioSource in step with the timeline: the heaviest
    /// clip's sound plays from its clip-local time, at its volume times its
    /// weight. Plays only while the timeline plays (in Play mode); seeking or
    /// pausing pauses it, and it picks up again at the right spot.
    /// </summary>
    [Serializable]
    public class GaussianAudioTrack : GaussianTimelineClipTrack
    {
        // Resync the source when it is this far off the timeline, in seconds
        private const double ResyncThreshold = 0.1;

        public override Type BindingType => typeof(AudioSource);

        public override Type ClipType => typeof(GaussianAudioClip);

        public override GaussianTimelineTrackInstance CreateInstance(GaussianTimelinePlayer player, Object binding)
        {
            return new Instance(this, (AudioSource)binding);
        }

        private sealed class Instance : GaussianTimelineTrackInstance
        {
            private readonly GaussianAudioTrack _track;
            private readonly AudioSource _source;
            private readonly AudioClip _ownClip;
            private readonly float _ownVolume;
            private readonly List<GaussianClipSample> _samples = new();
            private bool _driving;

            public Instance(GaussianAudioTrack track, AudioSource source)
            {
                _track = track;
                _source = source;
                _ownClip = source.clip;
                _ownVolume = source.volume;
            }

            public override void Evaluate(in GaussianTimelineEvaluation evaluation)
            {
                if (_source == null || !Application.isPlaying)
                    return;
                _track.Gather(evaluation, _samples);
                int strongest = GaussianTimelineClipTrack.Strongest(_samples);
                AudioClip audio = null;
                GaussianClipSample sample = default;
                if (strongest >= 0)
                {
                    sample = _samples[strongest];
                    audio = ((GaussianAudioClip)_track.Clips[sample.ClipIndex]).Audio;
                }
                if (audio == null || sample.LocalTime >= audio.length)
                {
                    if (_driving)
                        _source.Stop();
                    _driving = false;
                    return;
                }

                var clip = (GaussianAudioClip)_track.Clips[sample.ClipIndex];
                _source.volume = clip.Volume * sample.Weight;
                if (_source.clip != audio)
                {
                    _source.Stop();
                    _source.clip = audio;
                }
                if (!evaluation.Continuous)
                {
                    _source.Pause();
                    _source.time = (float)sample.LocalTime;
                }
                else if (!_source.isPlaying)
                {
                    _source.time = (float)sample.LocalTime;
                    _source.Play();
                }
                else if (Math.Abs(_source.time - sample.LocalTime) > ResyncThreshold)
                {
                    _source.time = (float)sample.LocalTime;
                }
                _driving = true;
            }

            public override void Restore()
            {
                if (_source == null)
                    return;
                if (_driving)
                    _source.Stop();
                _source.clip = _ownClip;
                _source.volume = _ownVolume;
                _driving = false;
            }
        }
    }
}
