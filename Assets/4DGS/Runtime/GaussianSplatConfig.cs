using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    [ExecuteInEditMode]
    public class GaussianSplatConfig : MonoBehaviour
    {
        [Header("Global Render Settings")]
        public GaussianSplatRenderMode renderMode = GaussianSplatRenderMode.Splats;
        [Range(1.0f, 15.0f)] public float pointDisplaySize = 3.0f;
        // Overall multiplier on the footprint-following point size in
        // PointCloud mode, applied to the footprint before the clamp below.
        [Range(0.1f, 4.0f)] public float pointCloudSizeScale = 1.0f;
        // Lower bound for footprint-following point size in PointCloud mode:
        // directly controls how big the smallest points render. Points under
        // 2 pixels are drawn 2 pixels wide and dimmer, so they do not alias.
        [Range(0.1f, 15.0f)] public float pointCloudMinDisplaySize = 3.0f;
        // World-space size floor (point diameter in meters) for PointCloud
        // mode. Suppresses points shrinking as the camera moves close
        // (grazing-angle splats collapse in projected size); the floor grows
        // in pixels the closer the point is, and fades out with distance.
        // 0 disables.
        [Range(0.0f, 0.5f)] public float pointCloudMinWorldSize = 0.0f;
        // Upper bound for footprint-following point size in PointCloud mode.
        // Points grow with the splat's projected footprint (keeps nearby
        // surfaces covered) but never beyond this, to preserve the point look.
        [Range(1.0f, 64.0f)] public float pointCloudMaxDisplaySize = 16.0f;
        // Pushes low-opacity gaussians towards solid points in PointCloud mode
        // while keeping opacity-driven effects (dissolve, cutouts) working.
        [Range(1.0f, 10.0f)] public float pointCloudOpacityBoost = 3.0f;
        // Replaces the beauty image with a raw data channel. PointCloud mode
        // only — the splat path has no equivalent nearest-sample resolve.
        public GaussianSplatAovMode pointCloudAov = GaussianSplatAovMode.None;
        // Multiplier on the depth AOV. A depth pass measured in metres runs past
        // whatever range the output can hold, and everything beyond it flattens
        // to one value; scaling brings the scene back inside. Divide by the same
        // number downstream to recover metres. 0.1 fits a 10 m scene into 1.0.
        [Range(0.001f, 10.0f)] public float pointCloudAovDepthScale = 1.0f;
        public bool useTileRenderer = true;

        [Header("Performance")]
        public GaussianSplatCompositeMode compositeMode = GaussianSplatCompositeMode.Offscreen;
        // Quad half-size in standard deviations. 2.55 matches the original
        // renderer; ~2.24 (sqrt 5) cuts quad area by about a quarter and is
        // perceptually very close — the usual choice for VR.
        [Range(1.5f, 3.5f)] public float splatQuadSigma = 2.55f;
        // Shrink every quad to where its own opacity fades out. Only removes
        // fragments the shader discards anyway, so the image is unchanged.
        public bool adaptiveSplatExtent = true;
        // Largest on-screen splat radius in pixels. A splat right in front of
        // the eye can cover the whole screen many times over; larger ones are
        // scaled down. 4096 leaves the original behaviour; ~512 for VR.
        [Range(32f, 4096f)] public float maxSplatPixelRadius = 4096f;
        // Upper bound on every renderer's SH order. 0 skips all SH reads in the
        // per-frame view-data pass (up to 192 bytes per splat): flat, view-
        // independent color for a large bandwidth saving on mobile GPUs.
        [Range(0, 3)] public int maxSHOrder = 3;

        [Header("Sorting")]
        // Radial keeps the order valid while the camera only rotates, which is
        // what lets VR sort less often than it renders.
        public GaussianSplatSortMetric sortMetric = GaussianSplatSortMetric.Radial;
        // With radial sorting, static splats are only re-sorted once the camera
        // has moved this far (meters, in the splat object's space) since the
        // last sort. 0 sorts every frame. Animated or morphing splats always
        // sort every frame (subject to their sortNthFrame).
        [Min(0f)] public float sortMoveThreshold = 0.01f;

        // ODS has no single view axis, so it always sorts radially.
        public bool UseRadialSort =>
            sortMetric == GaussianSplatSortMetric.Radial ||
            projectionMode == GaussianSplatProjectionMode.OmniDirectionalStereo;

        [Header("Omni-Directional Stereo (360 capture)")]
        // Projects the full sphere into one 2:1 equirect image instead of a
        // single frustum. Meant for Stereo360Capture, not for the viewport:
        // only splat centres go through it, so any scene geometry in the same
        // camera still renders with the ordinary perspective matrices.
        public GaussianSplatProjectionMode projectionMode = GaussianSplatProjectionMode.Perspective;
        // Signed offset along the ODS viewing-circle radius, in meters. The
        // capture drives this per eye (-IPD/2, then +IPD/2); 0 gives mono 360.
        [Range(-0.1f, 0.1f)] public float odsEyeOffset = 0.0f;
        // Elevation (degrees off the horizon) where stereo starts fading to
        // mono, and where it is fully mono. Disparity is geometrically
        // impossible looking straight up or down, so every ODS pipeline merges
        // the poles; without it the zenith and nadir fight the viewer's eyes.
        [Range(0.0f, 90.0f)] public float odsPoleMergeStart = 60.0f;
        [Range(0.0f, 90.0f)] public float odsPoleMergeEnd = 80.0f;

        [Header("Shader References")]
        [SerializeField] private Shader _shaderSplatsRef;
        [SerializeField] private Shader _shaderCompositeRef;
        [SerializeField] private Shader _shaderDebugPointsRef;
        [SerializeField] private Shader _shaderDebugBoxesRef;
        [SerializeField] private Shader _shaderStylizedRef;

        // Resolved at runtime (from serialized ref or Shader.Find fallback)
        private Shader _shaderSplats;
        private Shader _shaderComposite;
        private Shader _shaderDebugPoints;
        private Shader _shaderDebugBoxes;
        private Shader _shaderStylized;
        private ComputeShader _csSplatUtilities;
        private ComputeShader _csSplatSort;
        private ComputeShader _csPortableSort;
        private ComputeShader _csTileRender;

        public Shader ShaderSplats => _shaderSplats;
        public Shader ShaderComposite => _shaderComposite;
        public Shader ShaderDebugPoints => _shaderDebugPoints;
        public Shader ShaderDebugBoxes => _shaderDebugBoxes;
        public Shader ShaderStylized => _shaderStylized;
        public ComputeShader CsSplatUtilities => _csSplatUtilities;
        public ComputeShader CsSplatSort => _csSplatSort;
        public ComputeShader CsPortableSort => _csPortableSort;
        public ComputeShader CsTileRender => _csTileRender;

        public bool ResourcesValid =>
            _shaderSplats != null && _shaderComposite != null &&
            _shaderDebugPoints != null && _shaderDebugBoxes != null &&
            _shaderStylized != null &&
            _csSplatUtilities != null && SystemInfo.supportsComputeShaders;

        private void OnEnable()
        {
            LoadResources();
        }

        private void LoadResources()
        {
            // Prefer serialized references (survive shader stripping in builds),
            // fall back to Shader.Find (works in editor without manual assignment)
            _shaderSplats = _shaderSplatsRef != null
                ? _shaderSplatsRef
                : Shader.Find("Gaussian Splatting/Render Splats");
            _shaderComposite = _shaderCompositeRef != null
                ? _shaderCompositeRef
                : Shader.Find("Hidden/Gaussian Splatting/Composite");
            _shaderDebugPoints = _shaderDebugPointsRef != null
                ? _shaderDebugPointsRef
                : Shader.Find("Gaussian Splatting/Debug/Render Points");
            _shaderDebugBoxes = _shaderDebugBoxesRef != null
                ? _shaderDebugBoxesRef
                : Shader.Find("Gaussian Splatting/Debug/Render Boxes");
            _shaderStylized = _shaderStylizedRef != null
                ? _shaderStylizedRef
                : Shader.Find("Gaussian Splatting/Render Stylized");
            _csSplatUtilities = Resources.Load<ComputeShader>("SplatUtilities");
            _csSplatSort = Resources.Load<ComputeShader>("SplatSort");
            _csPortableSort = Resources.Load<ComputeShader>("SplatPortableSort");
            _csTileRender = Resources.Load<ComputeShader>("GaussianTileRender");

            // Diagnostic: report resource loading results
            Debug.Log($"[GaussianSplat][Config] LoadResources: " +
                      $"splats={(_shaderSplats != null ? "OK" : "MISSING")}, " +
                      $"composite={(_shaderComposite != null ? "OK" : "MISSING")}, " +
                      $"debugPts={(_shaderDebugPoints != null ? "OK" : "MISSING")}, " +
                      $"debugBox={(_shaderDebugBoxes != null ? "OK" : "MISSING")}, " +
                      $"stylized={(_shaderStylized != null ? "OK" : "MISSING")}, " +
                      $"compute={(_csSplatUtilities != null ? "OK" : "MISSING")}, " +
                      $"sort={(_csSplatSort != null ? "OK" : "MISSING")}, " +
                      $"tileRender={(_csTileRender != null ? "OK" : "MISSING")}, " +
                      $"computeSupport={SystemInfo.supportsComputeShaders}, " +
                      $"ResourcesValid={ResourcesValid}, " +
                      $"usedSerializedRefs={(_shaderSplatsRef != null ? "yes" : "no")}");
        }

#if UNITY_EDITOR
        private void Reset()
        {
            AutoAssignShaders();
        }

        private void OnValidate()
        {
            if (_shaderSplatsRef == null || _shaderStylizedRef == null)
                AutoAssignShaders();
        }

        private void AutoAssignShaders()
        {
            _shaderSplatsRef = Shader.Find("Gaussian Splatting/Render Splats");
            _shaderCompositeRef = Shader.Find("Hidden/Gaussian Splatting/Composite");
            _shaderDebugPointsRef = Shader.Find("Gaussian Splatting/Debug/Render Points");
            _shaderDebugBoxesRef = Shader.Find("Gaussian Splatting/Debug/Render Boxes");
            _shaderStylizedRef = Shader.Find("Gaussian Splatting/Render Stylized");
            if (_shaderSplatsRef != null)
                UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
