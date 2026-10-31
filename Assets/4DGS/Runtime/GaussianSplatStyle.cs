using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // A stylized look for splats, applied by a GaussianStyleVolume under a
    // GaussianStyler and blended in by the volume's weight. Applied per splat
    // in the view-data pass, so it works on every draw path, in stereo and in
    // Direct composite mode. Styles draw in the Splats and PointCloud render
    // modes.
    [CreateAssetMenu(menuName = "Gaussian Splats/Style", fileName = "SplatStyle", order = 2)]
    public class GaussianSplatStyle : ScriptableObject
    {
        internal const int RampResolution = 256;

        [Header("Color")]
        [SerializeField] private GaussianSplatStyleColorSource colorSource = GaussianSplatStyleColorSource.Original;
        [Tooltip("Base color across the ramp input, used when the color source is Ramp")]
        [SerializeField] private Gradient ramp = new()
        {
            colorKeys = new[] { new GradientColorKey(Color.black, 0f), new GradientColorKey(Color.white, 1f) },
            alphaKeys = new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) },
        };
        [SerializeField] private GaussianSplatStyleRampInput rampInput = GaussianSplatStyleRampInput.Luminance;
        [Tooltip("Camera distance (scene units) at the start of the ramp, for the Depth ramp input")]
        [SerializeField, Min(0f)] private float rampDepthNear = 0.5f;
        [Tooltip("Camera distance (scene units) at the end of the ramp, for the Depth ramp input")]
        [SerializeField, Min(0f)] private float rampDepthFar = 10f;
        [Tooltip("Brightness multiplier. Above 1 drives values past white, for bloom to pick up")]
        [SerializeField, Range(0f, 8f)] private float intensity = 1f;

        [Header("Rim")]
        [Tooltip("How far brightness follows the rim term: 0 ignores it, 1 leaves surfaces facing the camera dark and lights only grazing ones")]
        [SerializeField, Range(0f, 1f)] private float rimAmount;
        [Tooltip("Sharpness of the rim: higher keeps the light closer to silhouettes")]
        [SerializeField, Range(0.5f, 8f)] private float rimPower = 2f;

        [Header("Shape")]
        [SerializeField] private GaussianSplatStyleShape shape = GaussianSplatStyleShape.Base;
        [Tooltip("Dot diameter in pixels, for the Disc shape. Below 2 pixels a dot is drawn 2 pixels wide and dimmer, so it does not alias")]
        [SerializeField, Range(0.5f, 16f)] private float dotSize = 2f;
        [SerializeField] private GaussianSplatStyleStreakAxis streakAxis = GaussianSplatStyleStreakAxis.WorldAxis;
        [Tooltip("World direction streaks trail in, for the World Axis streak axis")]
        [SerializeField] private Vector3 streakDirection = Vector3.down;
        [Tooltip("Streak length in world units, for the World Axis and View Ray streak axes")]
        [SerializeField, Min(0f)] private float streakLength = 0.3f;
        [Tooltip("Seconds of motion a streak covers, for the Velocity streak axis")]
        [SerializeField, Min(0f)] private float streakTime = 0.05f;
        [Tooltip("Streak width in pixels. Below 2 pixels a streak is drawn 2 pixels wide and dimmer, so it does not alias")]
        [SerializeField, Range(0.5f, 8f)] private float streakWidth = 1f;
        [Tooltip("How fast a streak fades from the splat to its tail: higher keeps the light near the splat")]
        [SerializeField, Range(0.25f, 8f)] private float streakTailPower = 1.5f;
        [Tooltip("Smallest dot diameter or streak width in world units, for the Disc and Streak shapes. Their pixel size holds at a distance; up close this floor takes over, so they grow with the scene instead of shrinking into it. 0 turns it off")]
        [SerializeField, Min(0f)] private float minWorldSize;

        [Header("Quantize")]
        [SerializeField] private GaussianSplatStyleQuantize quantize = GaussianSplatStyleQuantize.None;
        [Tooltip("Distance between slices or grid points, in world units")]
        [SerializeField, Min(0.001f)] private float quantizeSpacing = 0.03f;
        [Tooltip("Fraction of the spacing kept around each slice or grid point; splats farther out fade away. 1 keeps every splat and only snaps")]
        [SerializeField, Range(0.01f, 1f)] private float quantizeBand = 1f;
        [Tooltip("World direction the slices are stacked along: up gives horizontal scan lines")]
        [SerializeField] private Vector3 sliceNormal = Vector3.up;
        [Tooltip("Shifts the slices along their normal, in world units; animate it to scroll them")]
        [SerializeField] private float sliceOffset;

        [Header("Thinning")]
        [Tooltip("Fraction of splats kept. The rest fade out with the style weight and stop costing anything at full weight")]
        [SerializeField, Range(0f, 1f)] private float keepFraction = 1f;

        [Header("Glint")]
        [Tooltip("Fraction of splats that flash now and then")]
        [SerializeField, Range(0f, 1f)] private float glintDensity;
        [Tooltip("Flashes per second for each glinting splat, on average")]
        [SerializeField, Range(0f, 10f)] private float glintRate = 1f;
        [Tooltip("Brightness added at the peak of a flash, as a multiple of the splat's color")]
        [SerializeField, Range(0f, 16f)] private float glintIntensity = 4f;

        [Header("Blend")]
        [Tooltip("0 blends like ordinary splats, 1 adds light without occluding what is behind. Fully additive splats skip sorting")]
        [SerializeField, Range(0f, 1f)] private float additivity;

        [NonSerialized] private Texture2D _rampTexture;
        [NonSerialized] private bool _rampDirty = true;

        public GaussianSplatStyleColorSource ColorSource => colorSource;
        public GaussianSplatStyleRampInput RampInput => rampInput;
        public float RampDepthNear => rampDepthNear;
        public float RampDepthFar => rampDepthFar;
        public float Intensity => intensity;
        public float RimAmount => rimAmount;
        public float RimPower => rimPower;
        public GaussianSplatStyleShape Shape => shape;
        public float DotSize => dotSize;
        public GaussianSplatStyleStreakAxis StreakAxis => streakAxis;
        public Vector3 StreakDirection => streakDirection.sqrMagnitude > 1e-8f ? streakDirection.normalized : Vector3.down;
        public float StreakLength => streakLength;
        public float StreakTime => streakTime;
        public float StreakWidth => streakWidth;
        public float StreakTailPower => streakTailPower;
        public float MinWorldSize => minWorldSize;
        public GaussianSplatStyleQuantize Quantize => quantize;
        public float QuantizeSpacing => quantizeSpacing;
        public float QuantizeBand => quantizeBand;
        public Vector3 SliceNormal => sliceNormal.sqrMagnitude > 1e-8f ? sliceNormal.normalized : Vector3.up;
        public float SliceOffset => sliceOffset;
        public float KeepFraction => keepFraction;
        public float GlintDensity => glintDensity;
        public float GlintRate => glintRate;
        public float GlintIntensity => glintIntensity;
        public float Additivity => additivity;

        // How far what the style draws can lie from the splat's own position,
        // in world units: the snap distance plus the streak length and the
        // world size floor's radius. Velocity streaks are as long as the
        // fastest splat moves in the streak time.
        public float Reach(float maxSpeed)
        {
            float snap = quantize switch
            {
                GaussianSplatStyleQuantize.Slices => quantizeSpacing * 0.5f,
                GaussianSplatStyleQuantize.Grid => quantizeSpacing * 0.5f * Mathf.Sqrt(3f),
                _ => 0f,
            };
            float extent = shape switch
            {
                GaussianSplatStyleShape.Disc => minWorldSize * 0.5f,
                GaussianSplatStyleShape.Streak => StreakReach(maxSpeed) + minWorldSize * 0.5f,
                _ => 0f,
            };
            return snap + extent;
        }

        private float StreakReach(float maxSpeed) =>
            streakAxis == GaussianSplatStyleStreakAxis.Velocity ? streakTime * maxSpeed : streakLength;

        // The ramp baked into a 1D lookup texture, holding the gradient's
        // values unconverted: splat colors are gamma space at this stage
        public Texture2D RampTexture
        {
            get
            {
                if (_rampTexture == null || _rampDirty)
                    BakeRamp();
                return _rampTexture;
            }
        }

        // Call after changing the ramp gradient from script
        public void MarkRampDirty() => _rampDirty = true;

        private void BakeRamp()
        {
            if (_rampTexture == null)
            {
                _rampTexture = new Texture2D(RampResolution, 1, TextureFormat.RGBAHalf, false, true)
                {
                    name = $"{name} Ramp",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            var pixels = new Color[RampResolution];
            for (int i = 0; i < RampResolution; i++)
                pixels[i] = ramp.Evaluate(i / (RampResolution - 1f));
            _rampTexture.SetPixels(pixels);
            _rampTexture.Apply(false);
            _rampDirty = false;
        }

        private void OnValidate()
        {
            rampDepthFar = Mathf.Max(rampDepthFar, rampDepthNear + 0.01f);
            _rampDirty = true;
        }

        private void OnDisable()
        {
            if (_rampTexture != null)
                DestroyImmediate(_rampTexture);
            _rampTexture = null;
        }
    }
}
