using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Modulates splat color with a procedural caustic/water-ripple pattern
    /// based on world-space coordinates. Multiple algorithm choices available.
    /// </summary>
    public class CausticModifier : GaussianAnimModifier
    {
        public enum CausticPattern
        {
            Voronoi = 0,
            SineWave = 1,
            Noise = 2
        }

        [Tooltip("Caustic pattern algorithm")]
        public CausticPattern pattern = CausticPattern.Voronoi;

        [Tooltip("Animation speed")]
        public float speed = 1f;

        [Tooltip("Cell spacing / spatial frequency")]
        [Range(0.2f, 10f)]
        public float cellSize = 2f;

        [Tooltip("Line sharpness — smaller = thinner lines, larger = broader glow")]
        [Range(0.01f, 1f)]
        public float lineWidth = 0.15f;

        [Tooltip("Noise pattern: highlight threshold — higher = smaller, sparser bright spots")]
        [Range(0f, 1f)]
        public float noiseThreshold = 0.5f;

        [Tooltip("Brightness of the caustic highlights")]
        [Range(0f, 3f)]
        public float intensity = 1f;

        [Tooltip("Color tint of the caustic highlights (HDR for bloom)")]
        [ColorUsage(false, true)]
        public Color tint = new Color(0.7f, 0.9f, 1f, 1f);

        public override int ModifierType => TypeCaustic;

        public override string[] GetParamLabels() => new[]
        {
            "Speed", "Cell Size", "Line Width", "Intensity",
            "Tint R", "Tint G", "Tint B", "Tint A",
            "Pattern", "Noise Threshold", "", "",
            "", "", "", ""
        };

        private float _phase;

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            _phase += AdvanceFillTime(time) * speed;
            p0 = new Vector4(speed, cellSize, lineWidth, intensity);
            p1 = new Vector4(tint.r, tint.g, tint.b, _phase);
            p2 = new Vector4((float)pattern, noiseThreshold, 0, 0);
            p3 = Vector4.zero;
        }

        public override void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            p0 = new Vector4(speed, cellSize, lineWidth, intensity);
            p1 = new Vector4(tint.r, tint.g, tint.b, tint.a);
            p2 = new Vector4((float)pattern, noiseThreshold, 0, 0);
            p3 = Vector4.zero;
        }

        public override void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3)
        {
            speed = p0.x;
            cellSize = p0.y;
            lineWidth = p0.z;
            intensity = p0.w;
            tint = new Color(p1.x, p1.y, p1.z, p1.w);
            pattern = (CausticPattern)Mathf.RoundToInt(p2.x);
            noiseThreshold = p2.y;
        }

        public override Vector4 LerpParams(int paramIndex, Vector4 from, Vector4 to, float t)
        {
            Vector4 result = base.LerpParams(paramIndex, from, to, t);
            if (paramIndex == 2)
                result.x = t < 0.5f ? from.x : to.x; // pattern is an enum — snap
            return result;
        }
    }
}
