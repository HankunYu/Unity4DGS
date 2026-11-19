using System;
using UnityEngine;
using UnityEngine.Serialization;

namespace GaussianSplatting.Runtime
{
    // A stylized look for splats, applied by a GaussianStyleVolume under a
    // GaussianStyler and blended in by the volume's weight. Applied per splat
    // in the view-data pass, so it works on every draw path, in stereo and in
    // Direct composite mode. Styles draw in the Splats and PointCloud render
    // modes.
    [CreateAssetMenu(menuName = "Gaussian Splatting/Style", fileName = "SplatStyle", order = 2)]
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
        [Tooltip("Start of the ramp in world units, for the Depth, World Axis and Volume Distance ramp inputs: a distance from the camera or from the volume's centre, or a position along the ramp axis")]
        [FormerlySerializedAs("rampDepthNear")]
        [SerializeField] private float rampNear = 0.5f;
        [Tooltip("End of the ramp in world units, for the Depth, World Axis and Volume Distance ramp inputs")]
        [FormerlySerializedAs("rampDepthFar")]
        [SerializeField] private float rampFar = 10f;
        [Tooltip("World direction the ramp runs along, for the World Axis ramp input: up colors by height")]
        [SerializeField] private Vector3 rampAxis = Vector3.up;
        [Tooltip("How far the ramp color replaces the splat's own color: 0 keeps the splat's color, 1 takes the ramp's")]
        [SerializeField, Range(0f, 1f)] private float rampBlend = 1f;
        [Tooltip("Scales the ramp color by the brightness of the splat's own color, so the ramp sets the hue and the splat keeps its light and dark")]
        [SerializeField] private bool rampKeepLuminance;
        [Tooltip("Brightness multiplier. Above 1 drives values past white, for bloom to pick up")]
        [SerializeField, Range(0f, 8f)] private float intensity = 1f;

        [Header("Rim")]
        [Tooltip("How far brightness follows the rim term: 0 ignores it, 1 leaves surfaces facing the camera dark and lights only grazing ones")]
        [SerializeField, Range(0f, 1f)] private float rimAmount;
        [Tooltip("Sharpness of the rim: higher keeps the light closer to silhouettes")]
        [SerializeField, Range(0.5f, 8f)] private float rimPower = 2f;
        [Tooltip("Light added at grazing angles, a Fresnel edge following the rim power. Unlike the rim amount, which only darkens surfaces facing the camera, it lifts edges past the base color")]
        [SerializeField, Range(0f, 8f)] private float rimGlow;
        [Tooltip("Color of the rim glow, multiplied with the splat's own color by the source tint")]
        [SerializeField] private Color rimColor = Color.white;
        [Tooltip("How far the rim glow takes each splat's own color, lifted to full brightness with its hue kept: 0 glows in the rim color alone, 1 in the splat's color times the rim color")]
        [SerializeField, Range(0f, 1f)] private float rimSourceTint = 1f;
        [Tooltip("How far a noise breaks up the rim: 0 keeps it an even band, 1 lets it come and go in patches. The rim amount, glow, fade and spill all follow the broken rim")]
        [SerializeField, Range(0f, 1f)] private float rimNoise;
        [Tooltip("Size of the noise patches, in the asset's units. The noise sits on each splat's rest position as stored in the asset, so it stays on a body a mesh driver deforms")]
        [SerializeField, Min(0.001f)] private float rimNoiseSize = 0.05f;
        [Tooltip("How fast the noise patches drift and change, in patch sizes per second")]
        [SerializeField, Min(0f)] private float rimNoiseSpeed = 0.3f;
        [Tooltip("Per-splat randomness in where the patches end: 0 keeps their edges smooth, 1 breaks them into scattered grains")]
        [SerializeField, Range(0f, 1f)] private float rimNoiseGrain;
        [Tooltip("How far the rim fades splats out, so silhouettes thin into scattered points instead of a solid line")]
        [SerializeField, Range(0f, 1f)] private float rimFade;
        [Tooltip("Farthest the rim pushes splats out along their surface normal (mesh splats' local +Z), in world units. Each splat goes its own random share of it, most a little and a few the whole way, so silhouettes spill a scatter of points")]
        [SerializeField, Min(0f)] private float rimSpill;

        [Header("Masks")]
        [Tooltip("How far the splat's baked occlusion darkens it: creases, armpits and the hollows under a chin or collar go dark, exposed surfaces stay. Needs a .meshsplat imported with Bake Occlusion and its mesh driver; without them nothing changes")]
        [SerializeField, Range(0f, 1f)] private float occlusionAmount;
        [Tooltip("Contrast of the occlusion mask: above 1 deepens and widens the dark, below 1 keeps only the deepest creases")]
        [SerializeField, Range(0.25f, 8f)] private float occlusionContrast = 1f;
        [Tooltip("How far the depth mask darkens the splat. The mask is white on the side of the style volume's centre that faces the camera and black behind it, so it follows the camera and whatever the depth reference follows")]
        [SerializeField, Range(0f, 1f)] private float depthAmount;
        [Tooltip("Depth the mask takes to go from white to black, in world units, centred on the depth reference: about the depth of a figure lights its near side and darkens its far side")]
        [SerializeField, Min(0.01f)] private float depthRange = 0.6f;
        [Tooltip("Moves the mask's middle away from the camera (towards it when negative), in world units; animate it to sweep the mask through the volume")]
        [SerializeField] private float depthOffset;
        [Tooltip("What depth is measured against: the centre of the style volume, one near and far side for all it holds, or the centre of the mesh each splat sits on (a mesh splat with a Mesh Driver), so every figure and prop is lit on its own near side")]
        [SerializeField] private GaussianSplatStyleDepthReference depthReference = GaussianSplatStyleDepthReference.VolumeCentre;

        [Header("Shape")]
        [SerializeField] private GaussianSplatStyleShape shape = GaussianSplatStyleShape.Base;
        [Tooltip("Dot diameter in pixels, for the Disc shape. Below 2 pixels a dot is drawn 2 pixels wide and dimmer, so it does not alias")]
        [SerializeField, Range(0.5f, 16f)] private float dotSize = 2f;
        [SerializeField] private GaussianSplatStyleStreakAxis streakAxis = GaussianSplatStyleStreakAxis.WorldAxis;
        [Tooltip("World direction streaks trail in, for the World Axis streak axis; the direction the hatching runs across, for the Surface streak axis")]
        [SerializeField] private Vector3 streakDirection = Vector3.down;
        [Tooltip("Streak length in world units, for the World Axis, View Ray and Surface streak axes")]
        [SerializeField, Min(0f)] private float streakLength = 0.3f;
        [Tooltip("Seconds of motion a streak covers, for the Velocity streak axis")]
        [SerializeField, Min(0f)] private float streakTime = 0.05f;
        [Tooltip("Streak width in pixels. Below 2 pixels a streak is drawn 2 pixels wide and dimmer, so it does not alias")]
        [SerializeField, Range(0.5f, 8f)] private float streakWidth = 1f;
        [Tooltip("How fast a streak fades from the splat to its tail: higher keeps the light near the splat, 0 keeps it even along the streak")]
        [SerializeField, Range(0f, 8f)] private float streakTailPower = 1.5f;
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
        [Tooltip("Camera distance (world units) the look is tuned at. Farther away, splats are thinned by the square of the distance ratio, so fixed-size dots and streaks keep the density and brightness on screen they have here instead of piling up into a blown-out blob. 0 turns it off")]
        [SerializeField, Min(0f)] private float densityDistance;

        [Header("Glint")]
        [Tooltip("Fraction of splats that flash now and then")]
        [SerializeField, Range(0f, 1f)] private float glintDensity;
        [Tooltip("Flashes per second for each glinting splat, on average")]
        [SerializeField, Range(0f, 10f)] private float glintRate = 1f;
        [Tooltip("Brightness added at the peak of a flash, as a multiple of the splat's color")]
        [SerializeField, Range(0f, 16f)] private float glintIntensity = 4f;

        [Header("Flow")]
        [Tooltip("How far brightness follows waves running through the splats: 0 leaves them alone, 1 lights only each wave and its fading tail. The space is split into columns along the flow direction, each with its own waves")]
        [SerializeField, Range(0f, 1f)] private float flowAmount;
        [Tooltip("World direction the waves run in")]
        [SerializeField] private Vector3 flowDirection = Vector3.down;
        [Tooltip("How fast the waves run, in world units per second. Each column runs at its own share of it")]
        [SerializeField, Min(0f)] private float flowSpeed = 0.5f;
        [Tooltip("Length of the lit tail behind each wave, in world units")]
        [SerializeField, Min(0.001f)] private float flowLength = 0.4f;
        [Tooltip("Distance from one wave to the next in a column, in world units. At or below the tail length the column never goes dark")]
        [SerializeField, Min(0.001f)] private float flowSpacing = 0.8f;
        [Tooltip("Width of a column, in world units. Match the quantize spacing on a Grid so each grid column carries its own waves")]
        [SerializeField, Min(0.001f)] private float flowColumnSize = 0.03f;
        [Tooltip("Steps the waves advance in, in world units: on a Grid, the quantize spacing lights whole cells one after another instead of sliding the brightness across them. 0 runs smoothly")]
        [SerializeField, Min(0f)] private float flowStep;

        [Header("Blend")]
        [Tooltip("0 blends like ordinary splats, 1 adds light without occluding what is behind. Fully additive splats skip sorting")]
        [SerializeField, Range(0f, 1f)] private float additivity;
        [Tooltip("Additive splats still hide what lies behind them under the renderer's occlusion culling, so they glow like a solid surface instead of adding up with the layers behind (e.g. clothes over clothes)")]
        [SerializeField] private bool occludes;

        [NonSerialized] private Texture2D _rampTexture;
        [NonSerialized] private bool _rampDirty = true;

        public GaussianSplatStyleColorSource ColorSource => colorSource;
        public GaussianSplatStyleRampInput RampInput => rampInput;
        public float RampNear => rampNear;
        public float RampFar => rampFar;
        public Vector3 RampAxis => rampAxis.sqrMagnitude > 1e-8f ? rampAxis.normalized : Vector3.up;
        public float RampBlend => rampBlend;
        public bool RampKeepLuminance => rampKeepLuminance;
        public float Intensity => intensity;
        public float RimAmount => rimAmount;
        public float RimPower => rimPower;
        public float RimGlow => rimGlow;
        public Color RimColor => rimColor;
        public float RimSourceTint => rimSourceTint;
        public float RimNoise => rimNoise;
        public float RimNoiseSize => rimNoiseSize;
        public float RimNoiseSpeed => rimNoiseSpeed;
        public float RimNoiseGrain => rimNoiseGrain;
        public float RimFade => rimFade;
        public float RimSpill => rimSpill;
        public float OcclusionAmount => occlusionAmount;
        public float OcclusionContrast => occlusionContrast;
        public float DepthAmount => depthAmount;
        public float DepthRange => depthRange;
        public float DepthOffset => depthOffset;
        public GaussianSplatStyleDepthReference DepthReference => depthReference;
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
        public float DensityDistance => densityDistance;
        public float GlintDensity => glintDensity;
        public float GlintRate => glintRate;
        public float GlintIntensity => glintIntensity;
        public float FlowAmount => flowAmount;
        public Vector3 FlowDirection => flowDirection.sqrMagnitude > 1e-8f ? flowDirection.normalized : Vector3.down;
        public float FlowSpeed => flowSpeed;
        public float FlowLength => flowLength;
        public float FlowSpacing => flowSpacing;
        public float FlowColumnSize => flowColumnSize;
        public float FlowStep => flowStep;
        public float Additivity => additivity;
        public bool Occludes => occludes;

        // How far what the style draws can lie from the splat's own position,
        // in world units: the snap distance and the rim spill plus the streak
        // length and the world size floor's radius. Velocity streaks are as long as the
        // fastest splat moves in the streak time, Segment streaks as the
        // longest segment.
        public float Reach(float maxSpeed, float maxSegment)
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
                GaussianSplatStyleShape.Streak => StreakReach(maxSpeed, maxSegment) + minWorldSize * 0.5f,
                _ => 0f,
            };
            return snap + rimSpill + extent;
        }

        private float StreakReach(float maxSpeed, float maxSegment) => streakAxis switch
        {
            GaussianSplatStyleStreakAxis.Velocity => streakTime * maxSpeed,
            GaussianSplatStyleStreakAxis.Segment => maxSegment,
            _ => streakLength,
        };

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
            rampFar = Mathf.Max(rampFar, rampNear + 0.01f);
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
