using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Runs a <see cref="GaussianParticleEmitter"/>'s clock from the
    /// timeline, so seeking shows the particles of that time and a capture
    /// gives the same particles on every take. The heaviest clip sets the
    /// time; between clips the emitter runs on by itself.
    /// </summary>
    [Serializable]
    public class GaussianParticleTrack : GaussianTimelineClipTrack
    {
        public override Type BindingType => typeof(GaussianParticleEmitter);

        public override Type ClipType => typeof(GaussianParticleClip);

        public override GaussianTimelineTrackInstance CreateInstance(GaussianTimelinePlayer player, Object binding)
        {
            return new Instance(this, (GaussianParticleEmitter)binding);
        }

        private sealed class Instance : GaussianTimelineTrackInstance
        {
            private readonly GaussianParticleTrack _track;
            private readonly GaussianParticleEmitter _emitter;
            private readonly List<GaussianClipSample> _samples = new();

            public Instance(GaussianParticleTrack track, GaussianParticleEmitter emitter)
            {
                _track = track;
                _emitter = emitter;
            }

            public override void Evaluate(in GaussianTimelineEvaluation evaluation)
            {
                if (_emitter == null)
                    return;
                _track.Gather(evaluation, _samples);
                int strongest = Strongest(_samples);
                if (strongest < 0)
                {
                    _emitter.StopDriving();
                    return;
                }
                GaussianClipSample sample = _samples[strongest];
                _emitter.Drive(((GaussianParticleClip)_track.Clips[sample.ClipIndex]).EmitterTime(sample.LocalTime));
            }

            public override void Restore()
            {
                if (_emitter != null)
                    _emitter.StopDriving();
            }
        }
    }
}
