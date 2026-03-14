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
        public bool useTileRenderer = true;

        [Header("Shader References")]
        [SerializeField] private Shader _shaderSplatsRef;
        [SerializeField] private Shader _shaderCompositeRef;
        [SerializeField] private Shader _shaderDebugPointsRef;
        [SerializeField] private Shader _shaderDebugBoxesRef;

        // Resolved at runtime (from serialized ref or Shader.Find fallback)
        private Shader _shaderSplats;
        private Shader _shaderComposite;
        private Shader _shaderDebugPoints;
        private Shader _shaderDebugBoxes;
        private ComputeShader _csSplatUtilities;
        private ComputeShader _csSplatSort;
        private ComputeShader _csTileRender;

        public Shader ShaderSplats => _shaderSplats;
        public Shader ShaderComposite => _shaderComposite;
        public Shader ShaderDebugPoints => _shaderDebugPoints;
        public Shader ShaderDebugBoxes => _shaderDebugBoxes;
        public ComputeShader CsSplatUtilities => _csSplatUtilities;
        public ComputeShader CsSplatSort => _csSplatSort;
        public ComputeShader CsTileRender => _csTileRender;

        public bool ResourcesValid =>
            _shaderSplats != null && _shaderComposite != null &&
            _shaderDebugPoints != null && _shaderDebugBoxes != null &&
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
            _csSplatUtilities = Resources.Load<ComputeShader>("SplatUtilities");
            _csSplatSort = Resources.Load<ComputeShader>("SplatSort");
            _csTileRender = Resources.Load<ComputeShader>("GaussianTileRender");

            // Diagnostic: report resource loading results
            Debug.Log($"[GaussianSplat][Config] LoadResources: " +
                      $"splats={(_shaderSplats != null ? "OK" : "MISSING")}, " +
                      $"composite={(_shaderComposite != null ? "OK" : "MISSING")}, " +
                      $"debugPts={(_shaderDebugPoints != null ? "OK" : "MISSING")}, " +
                      $"debugBox={(_shaderDebugBoxes != null ? "OK" : "MISSING")}, " +
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
            if (_shaderSplatsRef == null)
                AutoAssignShaders();
        }

        private void AutoAssignShaders()
        {
            _shaderSplatsRef = Shader.Find("Gaussian Splatting/Render Splats");
            _shaderCompositeRef = Shader.Find("Hidden/Gaussian Splatting/Composite");
            _shaderDebugPointsRef = Shader.Find("Gaussian Splatting/Debug/Render Points");
            _shaderDebugBoxesRef = Shader.Find("Gaussian Splatting/Debug/Render Boxes");
            if (_shaderSplatsRef != null)
                UnityEditor.EditorUtility.SetDirty(this);
        }
#endif
    }
}
