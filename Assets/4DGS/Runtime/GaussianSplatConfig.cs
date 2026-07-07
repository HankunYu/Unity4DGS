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
        // directly controls how big the smallest points render.
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
        public bool useTileRenderer = true;

        [Header("Shader References")]
        [SerializeField] private Shader _shaderSplatsRef;
        [SerializeField] private Shader _shaderCompositeRef;
        [SerializeField] private Shader _shaderDebugPointsRef;
        [SerializeField] private Shader _shaderDebugBoxesRef;
        [SerializeField] private Shader _shaderPointCloudRef;

        // Resolved at runtime (from serialized ref or Shader.Find fallback)
        private Shader _shaderSplats;
        private Shader _shaderComposite;
        private Shader _shaderDebugPoints;
        private Shader _shaderDebugBoxes;
        private Shader _shaderPointCloud;
        private ComputeShader _csSplatUtilities;
        private ComputeShader _csSplatSort;
        private ComputeShader _csCountingSort;
        private ComputeShader _csTileRender;

        public Shader ShaderSplats => _shaderSplats;
        public Shader ShaderComposite => _shaderComposite;
        public Shader ShaderDebugPoints => _shaderDebugPoints;
        public Shader ShaderDebugBoxes => _shaderDebugBoxes;
        public Shader ShaderPointCloud => _shaderPointCloud;
        public ComputeShader CsSplatUtilities => _csSplatUtilities;
        public ComputeShader CsSplatSort => _csSplatSort;
        public ComputeShader CsCountingSort => _csCountingSort;
        public ComputeShader CsTileRender => _csTileRender;

        public bool ResourcesValid =>
            _shaderSplats != null && _shaderComposite != null &&
            _shaderDebugPoints != null && _shaderDebugBoxes != null &&
            _shaderPointCloud != null &&
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
            _shaderPointCloud = _shaderPointCloudRef != null
                ? _shaderPointCloudRef
                : Shader.Find("Gaussian Splatting/Render Point Cloud");
            _csSplatUtilities = Resources.Load<ComputeShader>("SplatUtilities");
            _csSplatSort = Resources.Load<ComputeShader>("SplatSort");
            _csCountingSort = Resources.Load<ComputeShader>("SplatCountingSort");
            _csTileRender = Resources.Load<ComputeShader>("GaussianTileRender");

            // Diagnostic: report resource loading results
            Debug.Log($"[GaussianSplat][Config] LoadResources: " +
                      $"splats={(_shaderSplats != null ? "OK" : "MISSING")}, " +
                      $"composite={(_shaderComposite != null ? "OK" : "MISSING")}, " +
                      $"debugPts={(_shaderDebugPoints != null ? "OK" : "MISSING")}, " +
                      $"debugBox={(_shaderDebugBoxes != null ? "OK" : "MISSING")}, " +
                      $"pointCloud={(_shaderPointCloud != null ? "OK" : "MISSING")}, " +
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
            if (_shaderSplatsRef == null || _shaderPointCloudRef == null)
                AutoAssignShaders();
        }

        private void AutoAssignShaders()
        {
            _shaderSplatsRef = Shader.Find("Gaussian Splatting/Render Splats");
            _shaderCompositeRef = Shader.Find("Hidden/Gaussian Splatting/Composite");
            _shaderDebugPointsRef = Shader.Find("Gaussian Splatting/Debug/Render Points");
            _shaderDebugBoxesRef = Shader.Find("Gaussian Splatting/Debug/Render Boxes");
            _shaderPointCloudRef = Shader.Find("Gaussian Splatting/Render Point Cloud");
            if (_shaderSplatsRef != null)
                UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
