// SPDX-License-Identifier: MIT

using Unity.Collections;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Audio-reactive ripples expanding outward from a center transform.
    /// Bass and treble band energies are pushed into a history ring buffer
    /// each frame and uploaded through the modifier extra-data channel, so
    /// the waveform physically travels: a beat spawns a ring that keeps
    /// expanding while newer rings form behind it. Optionally tints splats
    /// with a bass/treble color mix scaled by the local ripple energy.
    /// </summary>
    public class AudioRippleModifier : GaussianAnimModifier
    {
        public enum RippleMode
        {
            Height = 0, // rings in the plane perpendicular to axis, displaced along axis
            Radial = 1  // spherical shells displaced away from the center
        }

        // Must match AUDIO_HISTORY_LEN in GaussianAnimate.compute
        public const int HistoryLength = 128;
        // Must match the treble carrier multiplier in GaussianAnimate.compute
        private const float TrebleRingScale = 2.7f;
        private const int SpectrumSize = 512;
        private const float SampleInterval = 0.02f; // 50 Hz history rate

        [Tooltip("Ripple origin. When null, this modifier's own transform is used.")]
        public Transform center;

        [Tooltip("Audio source to analyze. When null, the global AudioListener mix is used.")]
        public AudioSource audioSource;

        [Range(0f, 4f)]
        [Tooltip("Input gain applied to the analyzed audio bands")]
        public float audioGain = 1f;

        [Range(0f, 5f)]
        [Tooltip("Ripple displacement amplitude")]
        public float amplitude = 0.5f;

        [Range(0.1f, 10f)]
        [Tooltip("Ring density in rings per meter")]
        public float ringFrequency = 2f;

        [Range(0.1f, 10f)]
        [Tooltip("Outward propagation speed in meters per second")]
        public float waveSpeed = 1.5f;

        [Tooltip("Height: flat rings displaced along the axis. Radial: spherical shells pushed outward.")]
        public RippleMode mode = RippleMode.Height;

        [Tooltip("Displacement axis and ring plane normal for Height mode (auto-normalized)")]
        public Vector3 axis = Vector3.up;

        [Range(0f, 3f)]
        [Tooltip("Exponential amplitude falloff with distance from the center")]
        public float distanceFalloff = 0.4f;

        [Range(0f, 1f)]
        [Tooltip("Color response strength (0 = keep original splat colors)")]
        public float colorBlend = 0.5f;

        [Tooltip("Tint for low-frequency (bass) energy")]
        public Color bassColor = new Color(1f, 0.35f, 0.1f);

        [Tooltip("Tint for high-frequency (treble) energy")]
        public Color trebleColor = new Color(0.25f, 0.8f, 1f);

        [Tooltip("Drive the ripple with a synthetic beat while not in play mode")]
        public bool editorPreview = true;

        public override int ModifierType => TypeAudioRipple;

        public override string[] GetParamLabels() => new[]
        {
            "Axis X", "Axis Y", "Axis Z", "Amplitude",
            "Ring Freq", "Wave Speed", "Mode", "Falloff",
            "Bass R", "Bass G", "Bass B", "Color Blend",
            "Treble R", "Treble G", "Treble B", "Audio Gain"
        };

        private float[] _spectrum;
        private readonly float[] _history = new float[HistoryLength * 2]; // interleaved (low, high)
        private int _historyHead;
        private float _sampleAccum;
        private float _smoothedLow;
        private float _smoothedHigh;
        private float _phaseLow;
        private float _phaseHigh;
#if UNITY_EDITOR
        private float _previewTime;
#endif

        protected override void OnEnable()
        {
            base.OnEnable();
            System.Array.Clear(_history, 0, _history.Length);
            _historyHead = 0;
            _sampleAccum = 0f;
            _smoothedLow = 0f;
            _smoothedHigh = 0f;
        }

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            float dt = AdvanceFillTime(time);
            UpdateAudio(dt);

            // Carrier phases accumulate on the CPU so runtime speed/frequency
            // edits never jump the ring positions (see AdvanceFillTime docs).
            const float tau = 2f * Mathf.PI;
            _phaseLow = Mathf.Repeat(_phaseLow + dt * waveSpeed * ringFrequency * tau, tau);
            _phaseHigh = Mathf.Repeat(_phaseHigh + dt * waveSpeed * ringFrequency * TrebleRingScale * tau, tau);

            Vector3 c = (center != null ? center : transform).position;
            Vector3 ax = axis.sqrMagnitude > 1e-6f ? axis.normalized : Vector3.up;
            // Fractional head lets the GPU interpolate smoothly between pushes
            float headF = _historyHead - 1 + _sampleAccum / SampleInterval;

            // p0: center world position, amplitude
            p0 = new Vector4(c.x, c.y, c.z, amplitude);
            // p1: ring frequency, wave speed, history head, sample interval
            p1 = new Vector4(ringFrequency, waveSpeed, headF, SampleInterval);
            // p2: displacement axis, color blend
            p2 = new Vector4(ax.x, ax.y, ax.z, colorBlend);
            // p3: mode, distance falloff, accumulated carrier phases
            p3 = new Vector4((float)mode, distanceFalloff, _phaseLow, _phaseHigh);
        }

        public override int ExtraDataCount => 6 + HistoryLength * 2;

        public override void FillExtraData(NativeArray<float> dest, int offset)
        {
            dest[offset + 0] = bassColor.r;
            dest[offset + 1] = bassColor.g;
            dest[offset + 2] = bassColor.b;
            dest[offset + 3] = trebleColor.r;
            dest[offset + 4] = trebleColor.g;
            dest[offset + 5] = trebleColor.b;
            for (int i = 0; i < HistoryLength * 2; i++)
                dest[offset + 6 + i] = _history[i];
        }

        private void UpdateAudio(float dt)
        {
            float targetLow, targetHigh;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                if (editorPreview)
                {
                    // Synthetic ~110 BPM kick with off-beat hats so the effect
                    // is visible while authoring without audio playback
                    _previewTime += dt;
                    float beat = _previewTime * 1.83f;
                    float kick = Mathf.Exp(-6f * (beat - Mathf.Floor(beat)));
                    float hatPhase = beat * 2f + 0.5f;
                    float hat = Mathf.Exp(-9f * (hatPhase - Mathf.Floor(hatPhase)));
                    targetLow = kick;
                    targetHigh = hat * (0.5f + 0.35f * Mathf.Sin(_previewTime * 2.1f));
                }
                else
                {
                    targetLow = 0f;
                    targetHigh = 0f;
                }
            }
            else
#endif
            {
                ReadSpectrumBands(out targetLow, out targetHigh);
            }

            _smoothedLow = SmoothBand(_smoothedLow, targetLow, dt);
            _smoothedHigh = SmoothBand(_smoothedHigh, targetHigh, dt);

            // Push into the history ring at a fixed rate; cap the backlog so
            // a long frame hitch cannot flood the buffer with duplicates
            _sampleAccum = Mathf.Min(_sampleAccum + dt, SampleInterval * 8f);
            while (_sampleAccum >= SampleInterval)
            {
                int slot = (_historyHead % HistoryLength) * 2;
                _history[slot] = _smoothedLow;
                _history[slot + 1] = _smoothedHigh;
                _historyHead++;
                _sampleAccum -= SampleInterval;
            }
        }

        private void ReadSpectrumBands(out float low, out float high)
        {
            _spectrum ??= new float[SpectrumSize];
            if (audioSource != null)
                audioSource.GetSpectrumData(_spectrum, 0, FFTWindow.BlackmanHarris);
            else
                AudioListener.GetSpectrumData(_spectrum, 0, FFTWindow.BlackmanHarris);

            float binHz = AudioSettings.outputSampleRate * 0.5f / SpectrumSize;
            low = SumBand(20f, 250f, binHz);
            high = SumBand(2000f, 8000f, binHz);
            // Soft-knee compression keeps the response bounded in [0, 1)
            low = 1f - Mathf.Exp(-low * audioGain * 6f);
            high = 1f - Mathf.Exp(-high * audioGain * 20f);
        }

        private float SumBand(float fromHz, float toHz, float binHz)
        {
            int i0 = Mathf.Max(1, Mathf.FloorToInt(fromHz / binHz));
            int i1 = Mathf.Min(SpectrumSize - 1, Mathf.CeilToInt(toHz / binHz));
            float sum = 0f;
            for (int i = i0; i <= i1; i++)
                sum += _spectrum[i];
            return sum;
        }

        private static float SmoothBand(float current, float target, float dt)
        {
            // Fast attack so beats hit hard, slow release for a musical tail
            float smoothing = target > current ? 0.035f : 0.22f;
            return Mathf.Lerp(target, current, Mathf.Exp(-dt / smoothing));
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(axis.x, axis.y, axis.z, amplitude);
            p1 = new Vector4(ringFrequency, waveSpeed, (float)mode, distanceFalloff);
            p2 = new Vector4(bassColor.r, bassColor.g, bassColor.b, colorBlend);
            p3 = new Vector4(trebleColor.r, trebleColor.g, trebleColor.b, audioGain);
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            axis = new Vector3(p0.x, p0.y, p0.z);
            amplitude = p0.w;
            ringFrequency = p1.x;
            waveSpeed = p1.y;
            mode = (RippleMode)Mathf.RoundToInt(p1.z);
            distanceFalloff = p1.w;
            bassColor = new Color(p2.x, p2.y, p2.z, 1f);
            colorBlend = p2.w;
            trebleColor = new Color(p3.x, p3.y, p3.z, 1f);
            audioGain = p3.w;
        }

        public override Vector4 LerpParams(int paramIndex, Vector4 from, Vector4 to, float t)
        {
            Vector4 result = base.LerpParams(paramIndex, from, to, t);
            if (paramIndex == 1)
                result.z = t < 0.5f ? from.z : to.z; // mode is an enum — snap
            return result;
        }
    }
}
