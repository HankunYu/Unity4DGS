using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Applies fBm-based turbulent displacement to splats.
    /// Wind direction controls noise scrolling; zero wind falls back to pure temporal evolution.
    /// </summary>
    public class TurbulenceModifier : GaussianAnimModifier
    {
        [Range(0f, 5f)]
        [Tooltip("Displacement strength")]
        public float strength = 0.5f;

        [Range(0.1f, 10f)]
        [Tooltip("Base noise spatial frequency (higher = finer detail)")]
        public float frequency = 1f;

        [Range(1, 6)]
        [Tooltip("Number of fBm octaves (more = richer detail, heavier GPU)")]
        public int octaves = 3;

        [Range(1f, 4f)]
        [Tooltip("Frequency multiplier per octave")]
        public float lacunarity = 2f;

        [Range(0f, 1f)]
        [Tooltip("Amplitude decay per octave")]
        public float gain = 0.5f;

        [Tooltip("Noise scroll direction; magnitude controls speed. Zero = pure temporal evolution.")]
        public Vector3 windDirection = new Vector3(1f, 0f, 0f);

        public override int ModifierType => TypeTurbulence;

        public override string[] GetParamLabels() => new[]
        {
            "Strength", "Frequency", "Octaves", "Lacunarity",
            "Gain", "", "", "",
            "Wind X", "Wind Y", "Wind Z", "",
            "", "", "", ""
        };

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(strength, frequency, octaves, lacunarity);
            p1 = new Vector4(gain, time, 0f, 0f);
            p2 = new Vector4(windDirection.x, windDirection.y, windDirection.z, 0f);
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(strength, frequency, octaves, lacunarity);
            p1 = new Vector4(gain, 0f, 0f, 0f);
            p2 = new Vector4(windDirection.x, windDirection.y, windDirection.z, 0f);
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            strength = p0.x;
            frequency = p0.y;
            octaves = Mathf.RoundToInt(p0.z);
            lacunarity = p0.w;
            gain = p1.x;
            windDirection = new Vector3(p2.x, p2.y, p2.z);
        }
    }
}
