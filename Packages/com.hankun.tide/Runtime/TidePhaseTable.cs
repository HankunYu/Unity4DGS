using System;
using System.Collections.Generic;

namespace Tide
{
    /// <summary>
    /// The phases of the targets a timeline drives, at every frame of the
    /// timeline. Built by stepping the tracks that drive those targets
    /// through the timeline and summing the phase rates the targets report,
    /// so the phase at any time is a lookup: seeking, scrubbing and playback
    /// all show the same animation, and speed ramps stay smooth.
    /// </summary>
    public sealed class TidePhaseTable : ITidePhaseSource
    {
        private readonly TidePlayer _player;
        private readonly ITidePhaseTarget[] _targets;
        private readonly int[] _offsets;
        private readonly int _stride;
        private readonly int _frames;
        private readonly double _frameRate;
        // _frames rows of _stride phases
        private readonly double[] _table;

        /// <param name="evaluate">Poses the driving tracks at a timeline time.</param>
        public TidePhaseTable(TidePlayer player, IReadOnlyList<ITidePhaseTarget> targets,
            double duration, double frameRate, Action<double> evaluate)
        {
            _player = player;
            _frameRate = frameRate;
            _targets = new ITidePhaseTarget[targets.Count];
            _offsets = new int[targets.Count];
            int maxCount = 0;
            for (int m = 0; m < targets.Count; m++)
            {
                _targets[m] = targets[m];
                _offsets[m] = _stride;
                _stride += targets[m].PhaseCount;
                maxCount = Math.Max(maxCount, targets[m].PhaseCount);
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
                for (int m = 0; m < _targets.Length; m++)
                {
                    _targets[m].GetPhaseRates(rates);
                    int count = _targets[m].PhaseCount;
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

        public bool TryGetPhases(ITidePhaseTarget target, double[] phases)
        {
            int m = Array.IndexOf(_targets, target);
            if (m < 0)
                return false;
            double frame = Math.Clamp(_player.CurrentTime * _frameRate, 0.0, _frames - 1);
            int f0 = (int)frame;
            int f1 = Math.Min(f0 + 1, _frames - 1);
            double t = frame - f0;
            int count = Math.Min(phases.Length, target.PhaseCount);
            for (int k = 0; k < count; k++)
            {
                double a = _table[f0 * _stride + _offsets[m] + k];
                double b = _table[f1 * _stride + _offsets[m] + k];
                phases[k] = a + (b - a) * t;
            }
            return true;
        }

        /// <summary>Hands the targets back to their own accumulation.</summary>
        public void Release()
        {
            foreach (ITidePhaseTarget target in _targets)
            {
                // A destroyed component compares equal to null only as a Unity Object
                bool alive = target is UnityEngine.Object obj ? obj != null : target != null;
                if (alive && target.PhaseSource == this)
                    target.SetPhaseSource(null);
            }
        }

        public void Attach()
        {
            foreach (ITidePhaseTarget target in _targets)
                target.SetPhaseSource(this);
        }
    }
}
