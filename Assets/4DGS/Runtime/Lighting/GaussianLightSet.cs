using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // The lights, ambient and shadow map a renderer's splats are lit by, as
    // its GaussianSplatLighting set them up, packed for the shaders
    // (GaussianLighting.hlsl). The brightest shadow-casting directional or
    // spot light casts the shadow map, which the renderer fills from its own
    // splats (GaussianSplatRenderer.RenderLightShadow).
    internal sealed class GaussianLightSet
    {
        // Must match MAX_SPLAT_LIGHTS and LIGHT_SHADOW_HEADER in GaussianLighting.hlsl
        public const int MaxLights = 8;
        private const int ShadowHeader = 6;

        private static class Props
        {
            public static readonly int LightWeight = Shader.PropertyToID("_LightWeight");
            public static readonly int LightCount = Shader.PropertyToID("_LightCount");
            public static readonly int LightPositions = Shader.PropertyToID("_LightPositions");
            public static readonly int LightColors = Shader.PropertyToID("_LightColors");
            public static readonly int QuadKeyLight = Shader.PropertyToID("_QuadKeyLight");
            public static readonly int LightSpotDirections = Shader.PropertyToID("_LightSpotDirections");
            public static readonly int LightSpotParams = Shader.PropertyToID("_LightSpotParams");
            public static readonly int LightAmbient = Shader.PropertyToID("_LightAmbient");
            public static readonly int LightParams = Shader.PropertyToID("_LightParams");
            public static readonly int LightShadowMatrix = Shader.PropertyToID("_LightShadowMatrix");
            public static readonly int LightShadowParams = Shader.PropertyToID("_LightShadowParams");
            public static readonly int LightShadowResolution = Shader.PropertyToID("_LightShadowResolution");
            public static readonly int LightShadowMap = Shader.PropertyToID("_LightShadowMap");
            public static readonly int LightShadowMapRW = Shader.PropertyToID("_LightShadowMapRW");
        }

        private readonly Vector4[] _positions = new Vector4[MaxLights];
        private readonly Vector4[] _colors = new Vector4[MaxLights];
        private readonly Vector4[] _spotDirections = new Vector4[MaxLights];
        private readonly Vector4[] _spotParams = new Vector4[MaxLights];
        private readonly Vector4[] _ambient = new Vector4[7];
        private Vector4 _params;

        private int _shadowLight;
        private float _shadowLightPower;
        private int _strongestLight;
        private float _strongestLightPower;
        private Matrix4x4 _shadowMatrix;
        private bool _shadowPerspective;
        private bool _shadowsWanted;
        private float _shadowBias;
        private int _shadowFilter;
        private int _shadowResolution;
        private GraphicsBuffer _shadowMap;

        public float Weight { get; private set; }
        public int LightCount { get; private set; }

        public bool Active => Weight > 0f;
        public bool ShadowActive => Active && _shadowsWanted && _shadowLight >= 0;
        public int ShadowResolution => _shadowResolution;
        public int ShadowEntryCount => ShadowHeader + _shadowResolution * _shadowResolution;

        public void Begin(float weight)
        {
            Weight = Mathf.Clamp01(weight);
            LightCount = 0;
            _shadowLight = -1;
            _shadowLightPower = 0f;
            _strongestLight = -1;
            _strongestLightPower = 0f;
            _shadowsWanted = false;
        }

        // Terminator softness, and the ambient light around probePosition
        // (light probes, else the scene's ambient) scaled by ambientIntensity
        public void SetSurface(float wrap, float ambientIntensity, Vector3 probePosition)
        {
            _params.x = Mathf.Clamp01(wrap);
            LightProbes.GetInterpolatedProbe(probePosition, null, out SphericalHarmonicsL2 sh);
            sh *= ambientIntensity;
            // The unity_SHAr .. unity_SHC packing
            for (int c = 0; c < 3; c++)
            {
                _ambient[c] = new Vector4(sh[c, 3], sh[c, 1], sh[c, 2], sh[c, 0] - sh[c, 6]);
                _ambient[c + 3] = new Vector4(sh[c, 4], sh[c, 5], sh[c, 6] * 3f, sh[c, 7]);
            }
            _ambient[6] = new Vector4(sh[0, 8], sh[1, 8], sh[2, 8], 1f);
        }

        public void SetShadows(bool enabled, float strength, int resolution, float bias, int filter)
        {
            _shadowsWanted = enabled;
            _params.y = Mathf.Clamp01(strength);
            _shadowResolution = Mathf.Max(resolution, 16);
            _shadowBias = Mathf.Max(bias, 0f);
            _shadowFilter = Mathf.Max(filter, 0);
        }

        // False when the light gives nothing or the set is full
        public bool AddLight(Light light)
        {
            if (LightCount >= MaxLights || light.intensity <= 0f)
                return false;
            Color color = GraphicsSettings.lightsUseLinearIntensity
                ? light.color.linear * light.intensity
                : (light.color * light.intensity).linear;
            if (GraphicsSettings.lightsUseColorTemperature && light.useColorTemperature)
                color *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);

            int i = LightCount;
            Transform t = light.transform;
            Vector3 toLight = -t.forward;
            float invRangeSq = 1f / Mathf.Max(light.range * light.range, 1e-4f);
            switch (light.type)
            {
                case LightType.Directional:
                    _positions[i] = new Vector4(toLight.x, toLight.y, toLight.z, 0f);
                    _spotDirections[i] = Vector4.zero;
                    _spotParams[i] = new Vector4(0f, 1f, 0f, 0f);
                    break;
                case LightType.Point:
                    _positions[i] = new Vector4(t.position.x, t.position.y, t.position.z, 1f);
                    _spotDirections[i] = new Vector4(0f, 0f, 0f, invRangeSq);
                    _spotParams[i] = new Vector4(0f, 1f, 0f, 0f);
                    break;
                case LightType.Spot:
                    // URP's cone: full inside the inner angle, none past the outer
                    float cosOuter = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad);
                    float cosInner = Mathf.Cos(light.innerSpotAngle * 0.5f * Mathf.Deg2Rad);
                    float invAngleRange = 1f / Mathf.Max(cosInner - cosOuter, 1e-3f);
                    _positions[i] = new Vector4(t.position.x, t.position.y, t.position.z, 1f);
                    _spotDirections[i] = new Vector4(toLight.x, toLight.y, toLight.z, invRangeSq);
                    _spotParams[i] = new Vector4(invAngleRange, -cosOuter * invAngleRange, 0f, 0f);
                    break;
                default:
                    return false;
            }
            _colors[i] = new Vector4(color.r, color.g, color.b, 0f);
            LightCount++;

            float power = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
            if (power > _strongestLightPower)
            {
                _strongestLight = i;
                _strongestLightPower = power;
            }
            if (light.shadows != LightShadows.None && light.type != LightType.Point && power > _shadowLightPower)
            {
                _shadowLight = i;
                _shadowLightPower = power;
                _shadowPerspective = light.type == LightType.Spot;
                _shadowMatrix = Matrix4x4.TRS(t.position, t.rotation, Vector3.one).inverse;
            }
            return true;
        }

        // The lighting side (GaussianLighting.hlsl): for the view data, and
        // for any other compute shader that colors splats the way the
        // renderer draws them, e.g. GaussianParticleEmitter
        public void SetOnCS(CommandBuffer cmb, ComputeShader cs, int kernelIndex)
        {
            bool shadows = ShadowActive;
            for (int i = 0; i < LightCount; i++)
                _colors[i].w = shadows && i == _shadowLight ? 1f : 0f;
            cmb.SetComputeFloatParam(cs, Props.LightWeight, Weight);
            cmb.SetComputeIntParam(cs, Props.LightCount, LightCount);
            cmb.SetComputeVectorArrayParam(cs, Props.LightPositions, _positions);
            cmb.SetComputeVectorArrayParam(cs, Props.LightColors, _colors);
            cmb.SetComputeVectorArrayParam(cs, Props.LightSpotDirections, _spotDirections);
            cmb.SetComputeVectorArrayParam(cs, Props.LightSpotParams, _spotParams);
            cmb.SetComputeVectorArrayParam(cs, Props.LightAmbient, _ambient);
            cmb.SetComputeVectorParam(cs, Props.LightParams, _params);
            // The quad shader shades solid shapes (Sphere, Cube) by one
            // light: the one that casts the shadows, else the strongest
            int keyLight = _shadowLight >= 0 ? _shadowLight : _strongestLight;
            cmb.SetGlobalVector(Props.QuadKeyLight, Active && keyLight >= 0 ? _positions[keyLight] : Vector4.zero);
            SetShadowParamsOnCS(cmb, cs, shadows);
            EnsureShadowMap(shadows);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.LightShadowMap, _shadowMap);
        }

        // For the kernels that write the shadow map (CSLightShadow*)
        public void SetShadowTargetOnCS(CommandBuffer cmb, ComputeShader cs, int kernelIndex)
        {
            SetShadowParamsOnCS(cmb, cs, true);
            EnsureShadowMap(true);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.LightShadowMapRW, _shadowMap);
        }

        public void Dispose()
        {
            _shadowMap?.Dispose();
            _shadowMap = null;
        }

        private void SetShadowParamsOnCS(CommandBuffer cmb, ComputeShader cs, bool shadows)
        {
            cmb.SetComputeMatrixParam(cs, Props.LightShadowMatrix, _shadowMatrix);
            cmb.SetComputeVectorParam(cs, Props.LightShadowParams,
                new Vector4(shadows ? 1f : 0f, _shadowPerspective ? 1f : 0f, _shadowBias, _shadowFilter));
            cmb.SetComputeIntParam(cs, Props.LightShadowResolution, _shadowResolution);
        }

        // Sized for the resolution while shadows are on; otherwise whatever
        // exists, as the shaders only read it with shadows on
        private void EnsureShadowMap(bool shadows)
        {
            int count = shadows ? ShadowEntryCount : ShadowHeader;
            if (_shadowMap != null && (_shadowMap.count == count || (!shadows && _shadowMap.count >= count)))
                return;
            _shadowMap?.Dispose();
            _shadowMap = new GraphicsBuffer(GraphicsBuffer.Target.Raw, count, 4) { name = "GaussianLightShadowMap" };
            // Empty bounds until the first shadow pass: min above max
            _shadowMap.SetData(new uint[] { 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0, 0, 0 }, 0, 0, ShadowHeader);
        }
    }
}
