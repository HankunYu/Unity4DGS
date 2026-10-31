using System;
using System.Collections.Generic;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// The phases of the modifiers a timeline drives, at every frame of the
    /// timeline. Built by stepping the tracks that drive those modifiers
    /// through the timeline and summing the phase rates the modifiers report,
    /// so the phase at any time is a lookup: seeking, scrubbing and playback
    /// all show the same animation, and speed ramps stay smooth.
    /// </summary>
    public sealed class GaussianTimelinePhaseTable : IGaussianPhaseSource
    {
        private readonly GaussianTimelinePlayer _player;
        private readonly GaussianAnimModifier[] _modifiers;
        private readonly int[] _offsets;
        private readonly int _stride;
        private readonly int _frames;
        private readonly double _frameRate;
        // _frames rows of _stride phases
        private readonly double[] _table;

        /// <param name="evaluate">Poses the driving tracks at a timeline time.</param>
        public GaussianTimelinePhaseTable(GaussianTimelinePlayer player, IReadOnlyList<GaussianAnimModifier> modifiers,
            double duration, double frameRate, Action<double> evaluate)
        {
            _player = player;
            _frameRate = frameRate;
            _modifiers = new GaussianAnimModifier[modifiers.Count];
            _offsets = new int[modifiers.Count];
            int maxCount = 0;
            for (int m = 0; m < modifiers.Count; m++)
            {
                _modifiers[m] = modifiers[m];
                _offsets[m] = _stride;
                _stride += modifiers[m].PhaseCount;
                maxCount = Math.Max(maxCount, modifiers[m].PhaseCount);
            }
            _frames = (int)Math.Ceiling(duration * frameRate) + 1;
            _table = new double[_frames * _stride];

            // Trapezoids between frames' rates, exact for linear speed ramps
            var rates = new float[maxCount];
            var previousRates = new float[_stride];
            double dt = 1.0 / frameRate;
            for (int f = 0; f < _frames; f++)
            {
                evaluate(f * dt);
                int row = f * _stride;
                for (int m = 0; m < _modifiers.Length; m++)
                {
                    _modifiers[m].GetPhaseRates(rates);
                    int count = _modifiers[m].PhaseCount;
                    for (int k = 0; k < count; k++)
                    {
                        int column = _offsets[m] + k;
                        if (f > 0)
                            _table[row + column] = _table[row - _stride + column] + (previousRates[column] + rates[k]) * 0.5 * dt;
                        previousRates[column] = rates[k];
                    }
                }
            }
        }

        public bool TryGetPhases(GaussianAnimModifier modifier, double[] phases)
        {
            int m = Array.IndexOf(_modifiers, modifier);
            if (m < 0)
                return false;
            double frame = Math.Clamp(_player.CurrentTime * _frameRate, 0.0, _frames - 1);
            int f0 = (int)frame;
            int f1 = Math.Min(f0 + 1, _frames - 1);
            double t = frame - f0;
            int count = Math.Min(phases.Length, modifier.PhaseCount);
            for (int k = 0; k < count; k++)
            {
                double a = _table[f0 * _stride + _offsets[m] + k];
                double b = _table[f1 * _stride + _offsets[m] + k];
                phases[k] = a + (b - a) * t;
            }
            return true;
        }

        /// <summary>Hands the modifiers back to their own accumulation.</summary>
        public void Release()
        {
            foreach (GaussianAnimModifier modifier in _modifiers)
            {
                if (modifier != null && modifier.PhaseSource == this)
                    modifier.SetPhaseSource(null);
            }
        }

        public void Attach()
        {
            foreach (GaussianAnimModifier modifier in _modifiers)
                modifier.SetPhaseSource(this);
        }
    }
}
