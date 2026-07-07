using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Applies a wheat-field style travelling wave displacement to splats.
    /// Displacement is along Y axis, phase is computed from the XZ projection
    /// onto a configurable wave direction, ensuring coherent motion across
    /// neighbouring splats.
    /// </summary>
    public class WheatWaveModifier : GaussianAnimModifier
    {
        [Tooltip("Wave propagation direction in the XZ plane (normalized internally)")]
        public Vector2 waveDirection = new Vector2(1f, 0f);

        [Tooltip("Spatial frequency (higher = tighter waves)")]
        public float frequency = 1f;

        [Tooltip("Vertical displacement amplitude")]
        public float amplitude = 0.3f;

        [Tooltip("Wave travel speed")]
        public float speed = 1f;

        public override int ModifierType => TypeWheatWave;

        public override string[] GetParamLabels() => new[]
        {
            "Direction X", "Direction Y", "Frequency", "Amplitude",
            "Speed", "", "", "",
            "", "", "", "",
            "", "", "", ""
        };

        private float _phase;

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            _phase += AdvanceFillTime(time) * speed;
            Vector2 dir = waveDirection.sqrMagnitude > 0.001f
                ? waveDirection.normalized
                : Vector2.right;
            p0 = new Vector4(dir.x, dir.y, frequency, amplitude);
            p1 = new Vector4(speed, _phase, 0, 0);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(waveDirection.x, waveDirection.y, frequency, amplitude);
            p1 = new Vector4(speed, 0, 0, 0);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            waveDirection = new Vector2(p0.x, p0.y);
            frequency = p0.z;
            amplitude = p0.w;
            speed = p1.x;
        }
    }
}
