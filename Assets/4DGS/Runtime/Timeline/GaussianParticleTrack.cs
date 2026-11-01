using System;
using System.Collections.Generic;
using Object = UnityEngine.Object;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Runs a <see cref="GaussianParticleEmitter"/>'s clock from the
    /// timeline, so seeking shows the particles of that time and a capture
    /// gives the same particles on every take. The heaviest clip sets the
    /// time; between clips the emitter runs on by itself.
    /// </summary>
    [Serializable]
    [TideDisplay("Particle", 0.90f, 0.62f, 0.30f)]
    public class GaussianParticleTrack : TideClipTrack
    {
        public override Type BindingType => typeof(GaussianParticleEmitter);

        public override Type ClipType => typeof(GaussianParticleClip);

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            return new Instance(this, (GaussianParticleEmitter)binding);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly GaussianParticleTrack _track;
            private readonly GaussianParticleEmitter _emitter;
            private readonly List<TideClipSample> _samples = new();

            public Instance(GaussianParticleTrack track, GaussianParticleEmitter emitter)
            {
                _track = track;
                _emitter = emitter;
            }

            public override void Evaluate(in TideEvaluation evaluation)
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
                TideClipSample sample = _samples[strongest];
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
