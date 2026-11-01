// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Base class for all Gaussian Splat animation modifiers.
    /// Each modifier defines a type ID and packs its parameters into 4 Vector4s
    /// for upload to the GPU animation compute shader. Timelines drive the
    /// labelled parameter slots (see <see cref="GetParamLabels"/>) and Enabled
    /// as channels.
    /// </summary>
    public abstract class GaussianAnimModifier : MonoBehaviour, ITideChannelTarget, ITidePhaseTarget
    {
        public const int TypeDissolve = 0;
        public const int TypeWave = 1;
        public const int TypeWarp = 2;
        public const int TypeProperty = 3;
        public const int TypeCaustic = 4;
        public const int TypeWheatWave = 5;
        public const int TypeTurbulence = 6;
        public const int TypeSwirl = 7;
        public const int TypeConverge = 8;
        public const int TypeAudioRipple = 9;

        public const int ParamSlotCount = 16;

        private float _lastFillTime = -1f;
        // Accumulated phases (see PhaseCount), in double so hours of running keep their precision
        private double[] _phases;
        private float[] _phaseRates;
        private ITidePhaseSource _phaseSource;
        // Timeline channels: the labelled param slots, then Enabled
        private int[] _channelSlots;
        private string[] _channelLabels;
        private readonly Vector4[] _channelParams = new Vector4[4];

        public abstract int ModifierType { get; }

        /// <summary>
        /// How many phases this modifier accumulates. Time-driven modifiers
        /// must accumulate phase (phase += speed * dt) instead of packing
        /// absolute time and multiplying by speed on the GPU: with time * speed,
        /// any runtime speed change (state machine lerp, inspector edit, a
        /// timeline) shifts the phase by time * Δspeed and the animation
        /// visibly jumps — worse the longer the app has been running.
        /// </summary>
        public virtual int PhaseCount => 0;

        /// <summary>
        /// Per-second growth of each phase, from the current field values.
        /// <paramref name="rates"/> holds <see cref="PhaseCount"/> entries.
        /// </summary>
        public virtual void GetPhaseRates(float[] rates)
        {
        }

        /// <summary>
        /// Hands the phases to <paramref name="source"/> (a timeline playing
        /// this modifier, which knows the phase at any of its times), or back
        /// to accumulation with null.
        /// </summary>
        public void SetPhaseSource(ITidePhaseSource source)
        {
            _phaseSource = source;
        }

        public ITidePhaseSource PhaseSource => _phaseSource;

        /// <summary>The accumulated phase, as of the last <see cref="AdvanceFillTime"/>.</summary>
        protected double Phase(int index)
        {
            return _phases != null ? _phases[index] : 0.0;
        }

        /// <summary>
        /// Moves the phases on to <paramref name="time"/> and returns the time
        /// since the previous FillParams call. Call first thing in FillParams.
        /// </summary>
        protected float AdvanceFillTime(float time)
        {
            float dt = _lastFillTime >= 0f ? Mathf.Max(0f, time - _lastFillTime) : 0f;
            _lastFillTime = time;

            int count = PhaseCount;
            if (count == 0)
                return dt;
            if (_phases == null || _phases.Length != count)
            {
                _phases = new double[count];
                _phaseRates = new float[count];
            }
            if (_phaseSource != null && _phaseSource.TryGetPhases(this, _phases))
                return dt;
            GetPhaseRates(_phaseRates);
            for (int i = 0; i < count; i++)
                _phases[i] += _phaseRates[i] * (double)dt;
            return dt;
        }

        protected virtual void OnEnable()
        {
            // Reset so the first FillParams after a disabled period does not
            // advance phase by the entire disabled duration.
            _lastFillTime = -1f;
        }

        /// <summary>
        /// Returns 16 human-readable labels for the parameter slots (4 Vector4s × 4 components).
        /// Empty string means the slot is unused. Used by editors to display meaningful names
        /// instead of generic p0.x, p0.y, etc.
        /// Labels must match the packing order of <see cref="CaptureParams"/>.
        /// </summary>
        public virtual string[] GetParamLabels() => new string[ParamSlotCount];

        /// <summary>
        /// Pack modifier parameters into 4 Vector4s for GPU upload.
        /// Each modifier subclass interprets these differently.
        /// </summary>
        public abstract void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3);

        /// <summary>
        /// Number of floats this modifier uploads to the shared extra-data
        /// buffer each frame (0 = none). Use for variable-size payloads that
        /// do not fit the 4 packed Vector4s, e.g. audio history or LUTs.
        /// The GPU receives the segment via extraOffset/extraCount in the
        /// modifier struct.
        /// </summary>
        public virtual int ExtraDataCount => 0;

        /// <summary>
        /// Write exactly <see cref="ExtraDataCount"/> floats into
        /// <paramref name="dest"/> starting at <paramref name="offset"/>.
        /// Called once per frame right after <see cref="FillParams"/>.
        /// </summary>
        public virtual void FillExtraData(NativeArray<float> dest, int offset) { }

        /// <summary>
        /// Capture current serialized field values into 4 Vector4s for state machine snapshots.
        /// Unlike <see cref="FillParams"/>, this captures raw field values without time injection
        /// or normalization, making them suitable for interpolation between states.
        /// </summary>
        public abstract void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3);

        /// <summary>
        /// Apply parameter values from a state machine snapshot back to serialized fields.
        /// Values may be interpolated between states, so enum-like or discrete values
        /// should be rounded to the nearest valid value before assignment.
        /// </summary>
        public abstract void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3);

        /// <summary>
        /// Interpolate one captured param slot for state machine transitions.
        /// Subclasses whose <see cref="CaptureParams"/> layout contains discrete
        /// values (enums) must override and snap those components (switch at
        /// t = 0.5) — lerping them would pass through unrelated enum values.
        /// </summary>
        public virtual Vector4 LerpParams(int paramIndex, Vector4 from, Vector4 to, float t)
        {
            return Vector4.Lerp(from, to, t);
        }

        // ------------------------------------------------------------------ timeline channels

        int ITideChannelTarget.ChannelCount => ChannelSlots.Length + 1;

        string ITideChannelTarget.GetChannelName(int index)
        {
            return index < ChannelSlots.Length ? _channelLabels[ChannelSlots[index]] : "Enabled";
        }

        float ITideChannelTarget.GetChannelValue(int index)
        {
            if (index >= ChannelSlots.Length)
                return enabled ? 1f : 0f;
            int slot = ChannelSlots[index];
            CaptureParams(out _channelParams[0], out _channelParams[1], out _channelParams[2], out _channelParams[3]);
            return _channelParams[slot / 4][slot % 4];
        }

        void ITideChannelTarget.SetChannelValue(int index, float value)
        {
            if (index >= ChannelSlots.Length)
            {
                enabled = value >= 0.5f;
                return;
            }
            int slot = ChannelSlots[index];
            CaptureParams(out _channelParams[0], out _channelParams[1], out _channelParams[2], out _channelParams[3]);
            _channelParams[slot / 4][slot % 4] = value;
            ApplyParams(_channelParams[0], _channelParams[1], _channelParams[2], _channelParams[3]);
        }

        float ITideChannelTarget.BlendChannel(int index, float from, float to, float t)
        {
            if (index >= ChannelSlots.Length)
                return t < 0.5f ? from : to;
            // LerpParams knows which slots are discrete
            int slot = ChannelSlots[index];
            Vector4 a = default;
            Vector4 b = default;
            a[slot % 4] = from;
            b[slot % 4] = to;
            return LerpParams(slot / 4, a, b, t)[slot % 4];
        }

        private int[] ChannelSlots
        {
            get
            {
                if (_channelSlots != null)
                    return _channelSlots;
                _channelLabels = GetParamLabels();
                var slots = new List<int>();
                for (int i = 0; i < _channelLabels.Length && i < ParamSlotCount; i++)
                {
                    if (!string.IsNullOrEmpty(_channelLabels[i]))
                        slots.Add(i);
                }
                _channelSlots = slots.ToArray();
                return _channelSlots;
            }
        }
    }
}
