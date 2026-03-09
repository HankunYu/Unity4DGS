using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [ExecuteInEditMode]
    public class GaussianSplatConfig : MonoBehaviour
    {
        [Header("Global Render Settings")]
        public GaussianSplatRenderMode renderMode = GaussianSplatRenderMode.Splats;
        [Range(1.0f, 15.0f)] public float pointDisplaySize = 3.0f;
        public bool useTileRenderer = true;

        [Header("VR Settings")]
        [Tooltip("Use VR-optimized rendering path (RenderMeshPrimitives + vertex covariance)")]
        public bool useVRRenderPath;

        // Auto-loaded resources (not serialized)
        private Shader _shaderSplats;
        private Shader _shaderComposite;
        private Shader _shaderDebugPoints;
        private Shader _shaderDebugBoxes;
        private ComputeShader _csSplatUtilities;
        private ComputeShader _csTileRender;
        private Shader _shaderSplatsVR;

        public Shader ShaderSplats => _shaderSplats;
        public Shader ShaderComposite => _shaderComposite;
        public Shader ShaderDebugPoints => _shaderDebugPoints;
        public Shader ShaderDebugBoxes => _shaderDebugBoxes;
        public ComputeShader CsSplatUtilities => _csSplatUtilities;
        public ComputeShader CsTileRender => _csTileRender;
        public Shader ShaderSplatsVR => _shaderSplatsVR;

        public bool ResourcesValid =>
            _shaderSplats != null && _shaderComposite != null &&
            _shaderDebugPoints != null && _shaderDebugBoxes != null &&
            _csSplatUtilities != null && SystemInfo.supportsComputeShaders;

        private void OnEnable()
        {
            LoadResources();
        }

        private void LateUpdate()
        {
            if (useVRRenderPath)
                GaussianSplatRenderSystem.instance?.RenderVRSplats();
        }

        private void LoadResources()
        {
            _shaderSplats = Shader.Find("Gaussian Splatting/Render Splats");
            _shaderComposite = Shader.Find("Hidden/Gaussian Splatting/Composite");
            _shaderDebugPoints = Shader.Find("Gaussian Splatting/Debug/Render Points");
            _shaderDebugBoxes = Shader.Find("Gaussian Splatting/Debug/Render Boxes");
            _csSplatUtilities = Resources.Load<ComputeShader>("SplatUtilities");
            _csTileRender = Resources.Load<ComputeShader>("GaussianTileRender");
            _shaderSplatsVR = Shader.Find("Gaussian Splatting/Render Splats VR");
        }
    }
}
