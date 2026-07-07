// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Applies periodic wave displacement to splats.
    /// Supports sine waves, radial waves, and noise-based oscillation.
    /// </summary>
    public class WaveModifier : GaussianAnimModifier
    {
        public enum WaveType
        {
            Sine = 0,
            Radial = 1,
            Noise = 2
        }

        [Tooltip("Wave propagation direction (auto-normalized on GPU)")]
        public Vector3 waveAxis = Vector3.up;

        [Range(0f, 5f)]
        [Tooltip("Wave displacement amplitude")]
        public float amplitude = 0.5f;

        [Range(0.1f, 10f)]
        [Tooltip("Spatial frequency of the wave")]
        public float frequency = 1f;

        [Range(0f, 10f)]
        [Tooltip("Temporal speed of wave propagation")]
        public float speed = 1f;

        [Tooltip("Type of wave pattern")]
        public WaveType waveType = WaveType.Sine;

        public override int ModifierType => TypeWave;

        public override string[] GetParamLabels() => new[]
        {
            "Axis X", "Axis Y", "Axis Z", "Amplitude",
            "Frequency", "Speed", "Wave Type", "",
            "", "", "", "",
            "", "", "", ""
        };

        private float _phase;

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            _phase += AdvanceFillTime(time) * speed;
            Vector3 axis = waveAxis.normalized;
            // p0: wave axis xyz, amplitude
            p0 = new Vector4(axis.x, axis.y, axis.z, amplitude);
            // p1: frequency, speed, accumulated phase, waveType
            p1 = new Vector4(frequency, speed, _phase, (float)waveType);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(waveAxis.x, waveAxis.y, waveAxis.z, amplitude);
            p1 = new Vector4(frequency, speed, (float)waveType, 0);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            waveAxis = new Vector3(p0.x, p0.y, p0.z);
            amplitude = p0.w;
            frequency = p1.x;
            speed = p1.y;
            waveType = (WaveType)Mathf.RoundToInt(p1.z);
        }

        public override Vector4 LerpParams(int paramIndex, Vector4 from, Vector4 to, float t)
        {
            Vector4 result = base.LerpParams(paramIndex, from, to, t);
            if (paramIndex == 1)
                result.z = t < 0.5f ? from.z : to.z; // waveType is an enum — snap
            return result;
        }
    }
}
