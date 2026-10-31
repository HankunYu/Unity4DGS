using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Particles drifting off another splat renderer, the source. Each
    // particle is born at a random splat of the source, as it is drawn right
    // now, with the color it is drawn in (style included) and the velocity of
    // that spot of the surface plus a push along its normal, so a moving body
    // throws particles off. It is then simulated step by step, its velocity
    // settling into a flow of wind plus curl noise; its color and opacity
    // follow a curve over the life, it shrinks, and it is born again
    // elsewhere. The particles are this GameObject's own splats: the emitter
    // gives its GaussianSplatRenderer a generated asset of `capacity` splats
    // and drives their pose, size, opacity, color and velocity on the GPU
    // each frame, so sorting, styles (from a GaussianStyler of its own, e.g.
    // with Velocity streaks, or the source's), ODS capture and depth of field
    // treat them like any other splats, and the source is untouched.
    //
    // Runs after the source's mesh driver and animator in LateUpdate, and on
    // every render in edit mode.
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianParticleEmitter : MonoBehaviour, IGaussianTimelineTarget
    {
        private static readonly int PropParticleCount = Shader.PropertyToID("_ParticleCount");
        private static readonly int PropParticleSeed = Shader.PropertyToID("_ParticleSeed");
        private static readonly int PropParticleTime = Shader.PropertyToID("_ParticleTime");
        private static readonly int PropParticleDeltaTime = Shader.PropertyToID("_ParticleDeltaTime");
        private static readonly int PropParticleLifetime = Shader.PropertyToID("_ParticleLifetime");
        private static readonly int PropParticleEmission = Shader.PropertyToID("_ParticleEmission");
        private static readonly int PropParticleBirth = Shader.PropertyToID("_ParticleBirth");
        private static readonly int PropParticleWind = Shader.PropertyToID("_ParticleWind");
        private static readonly int PropParticleFlow = Shader.PropertyToID("_ParticleFlow");
        private static readonly int PropParticleShape = Shader.PropertyToID("_ParticleShape");
        private static readonly int PropParticleLook = Shader.PropertyToID("_ParticleLook");
        private static readonly int PropParticleWorldToObject = Shader.PropertyToID("_ParticleWorldToObject");
        private static readonly int PropParticleObjectScale = Shader.PropertyToID("_ParticleObjectScale");
        private static readonly int PropSourceObjectToWorld = Shader.PropertyToID("_SourceObjectToWorld");
        private static readonly int PropSourcePrev = Shader.PropertyToID("_SourcePrev");
        private static readonly int PropMatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
        private static readonly int PropCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
        private static readonly int PropParticleState = Shader.PropertyToID("_ParticleState");
        private static readonly int PropParticleBase = Shader.PropertyToID("_ParticleBase");
        private static readonly int PropParticleAnim = Shader.PropertyToID("_ParticleAnim");
        private static readonly int PropParticleVelocity = Shader.PropertyToID("_ParticleVelocity");
        private static readonly int PropParticleColorOverLife = Shader.PropertyToID("_ParticleColorOverLife");

        private const int GroupSize = 256;
        // Splat scale of the template's splats; the particle size scales it
        private const float TemplateScale = 0.001f;
        // Longest step the clock takes in edit mode, where renders are irregular
        private const float MaxEditStep = 0.1f;
        // Shortest step the source velocity is measured over, and the fastest
        // a particle inherits: a pose that jumps (a scrubbed timeline, two
        // renders around one update) must not fling particles away
        private const float MinVelocityStep = 1f / 240f;
        private const float MaxInheritedSpeed = 3f;
        // Must match COLOR_OVER_LIFE_SAMPLES in GaussianParticles.compute
        private const int ColorOverLifeSamples = 32;

        private static readonly string[] TimelineChannels = { "Emission", "Opacity", "Turbulence" };

        [Tooltip("Renderer the particles are born on")]
        [SerializeField] private GaussianSplatRenderer source;
        [Tooltip("Number of particles, alive or between lives")]
        [SerializeField, Min(1)] private int capacity = 100000;
        [Tooltip("Varies which splats the particles are born on")]
        [SerializeField] private int seed;
        [Tooltip("Style the particles with the source's GaussianStyler and its volumes, instead of this object's own")]
        [SerializeField] private bool followSourceStyle;

        [Header("Life")]
        [Tooltip("Seconds a particle lives, varied by +-25% per particle")]
        [SerializeField, Min(0.05f)] private float lifetime = 2.5f;
        [Tooltip("Fraction of lives that show; animate it to start and stop the effect")]
        [SerializeField, Range(0f, 1f)] private float emission = 1f;

        [Header("Birth")]
        [Tooltip("Fraction of the velocity of the source surface a particle starts with: a moving body throws particles off and trails them")]
        [SerializeField, Range(0f, 2f)] private float inheritVelocity = 1f;
        [Tooltip("World units per second a particle is pushed off along the surface normal (the splat's local +Z, which mesh splats align with the surface)")]
        [SerializeField, Min(0f)] private float normalSpeed = 0.1f;

        [Header("Flow")]
        [Tooltip("World units per second the flow carries particles along: up for rising embers, zero to hover")]
        [SerializeField] private Vector3 wind = new(0f, 0.1f, 0f);
        [Tooltip("How fast (per second) a particle's velocity settles into the flow: low keeps thrown particles flying, high makes them follow the streams closely")]
        [SerializeField, Min(0f)] private float drag = 1f;
        [Tooltip("Speed of the curl noise swirls, in world units per second")]
        [SerializeField, Min(0f)] private float turbulence = 1f;
        [Tooltip("Spatial frequency of the swirls: higher gives smaller, tighter streams")]
        [SerializeField, Min(0f)] private float turbulenceFrequency = 1.5f;
        [Tooltip("How fast the swirl pattern drifts and changes")]
        [SerializeField, Min(0f)] private float turbulenceSpeed = 0.3f;

        [Header("Look")]
        [Tooltip("Average size of a new particle, as a splat scale (Gaussian sigma) in world units")]
        [SerializeField, Min(0f)] private float size = 0.0015f;
        [Tooltip("0 makes every particle the same size; 1 makes most small and a few up to three times the average")]
        [SerializeField, Range(0f, 1f)] private float sizeVariation = 0.7f;
        [Tooltip("Size at the end of a life, as a fraction of the starting size")]
        [SerializeField, Range(0f, 2f)] private float endSize = 0.4f;
        [Tooltip("Multiplies the opacity a particle takes from its source splat")]
        [SerializeField, Range(0f, 4f)] private float opacity = 1f;
        [Tooltip("Color (above 1 glows with bloom) and opacity over a life, from birth on the left to death on the right")]
        [SerializeField, GradientUsage(true)] private Gradient colorOverLife = new()
        {
            colorKeys = new[]
            {
                new GradientColorKey(new Color(3f, 2.4f, 1.8f), 0f),
                new GradientColorKey(new Color(2.2f, 1.1f, 0.5f), 0.12f),
                new GradientColorKey(new Color(1f, 0.6f, 0.6f), 0.4f),
                new GradientColorKey(new Color(0.4f, 0.45f, 0.6f), 1f),
            },
            alphaKeys = new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(1f, 0.04f),
                new GradientAlphaKey(1f, 0.5f),
                new GradientAlphaKey(0f, 1f),
            },
        };
        [Tooltip("How much the color over life is tinted by the color the splat a particle was born on is drawn in, style included: 0 ignores it, 1 multiplies by it")]
        [SerializeField, Range(0f, 1f)] private float sourceColor = 1f;
        [Tooltip("Brightens dark source colors while keeping their hue, so particles off a dark surface still show: 0 takes them as drawn, 1 brings every color up to full brightness")]
        [SerializeField, Range(0f, 1f)] private float sourceLift;

        private GaussianSplatRenderer _renderer;
        private ComputeShader _shader;
        private int _kernel = -1;
        private int _kernelStore = -1;
        private CommandBuffer _cmd;

        private GaussianSplatAsset _template;
        private readonly List<TextAsset> _templateData = new();
        private GraphicsBuffer _state;
        private GraphicsBuffer _sourcePrev;
        private GraphicsBuffer _base;
        private GraphicsBuffer _anim;
        private GraphicsBuffer _velocity;
        private readonly Vector4[] _colorOverLifeSamples = new Vector4[ColorOverLifeSamples];

        private double _time;
        private double _lastEditTime = -1.0;

        public GaussianSplatRenderer Source
        {
            get => source;
            set => source = value;
        }

        /// <summary>Seconds on the emitter's clock, which drives every life.</summary>
        public double Time
        {
            get => _time;
            set => _time = Math.Max(0.0, value);
        }

        private void OnEnable()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            _shader = Resources.Load<ComputeShader>("GaussianParticles");
            if (_shader != null)
            {
                _kernel = _shader.FindKernel("CSUpdateParticles");
                _kernelStore = _shader.FindKernel("CSStoreSource");
            }
            _time = 0.0;
            _lastEditTime = -1.0;
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            ReleaseResources();
        }

        private void OnValidate()
        {
            capacity = Mathf.Max(1, capacity);
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            Step(UnityEngine.Time.deltaTime);
        }

        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (Application.isPlaying)
                return;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            float deltaTime = _lastEditTime >= 0.0 ? (float)Math.Min(now - _lastEditTime, MaxEditStep) : 0f;
            _lastEditTime = now;
            Step(deltaTime);
        }

        // Advances the clock and simulates the particles over that time.
        // Runs on its own (see above); call it after changing the source at
        // another point, with 0 to only pick up the source's new pose.
        public void Step(float deltaTime)
        {
            deltaTime = Mathf.Max(0f, deltaTime);
            _time += deltaTime;
            // Without the particle buffers the template would draw as is:
            // hand the renderer back empty instead
            if (!CanEmit())
            {
                ReleaseResources();
                return;
            }
            EnsureResources();
            _renderer.StylerOverride = followSourceStyle ? source.Styler : null;

            Transform t = transform;
            Vector3 scale = t.lossyScale;
            float objectScale = 1f / TemplateScale / Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Max(Mathf.Abs(scale.z), 1e-6f));

            // Births measure the source's velocity against its pose at the
            // previous step
            float velocityScale = inheritVelocity / Mathf.Max(deltaTime, MinVelocityStep);
            for (int i = 0; i < ColorOverLifeSamples; i++)
            {
                Color c = colorOverLife.Evaluate(i / (ColorOverLifeSamples - 1f));
                _colorOverLifeSamples[i] = new Vector4(c.r, c.g, c.b, c.a);
            }

            // The source's view-dependent style terms (rim, depth ramp) are
            // judged from the main camera
            Camera cam = Camera.main;
            Vector3 cameraPos = cam != null ? cam.transform.position : source.transform.position;

            _cmd.Clear();
            // A hidden source is not drawn, so nothing else refreshes its style
            source.RefreshStyle();
            source.SetAssetDataOnCS(_cmd, _shader, _kernel);
            source.SetStyleColorOnCS(_cmd, _shader, _kernel);
            _cmd.SetComputeMatrixParam(_shader, PropSourceObjectToWorld, source.transform.localToWorldMatrix);
            _cmd.SetComputeMatrixParam(_shader, PropMatrixWorldToObject, source.transform.worldToLocalMatrix);
            _cmd.SetComputeVectorParam(_shader, PropCameraPos, cameraPos);
            _cmd.SetComputeIntParam(_shader, PropParticleCount, capacity);
            _cmd.SetComputeIntParam(_shader, PropParticleSeed, seed);
            _cmd.SetComputeFloatParam(_shader, PropParticleTime, (float)_time);
            _cmd.SetComputeFloatParam(_shader, PropParticleDeltaTime, deltaTime);
            _cmd.SetComputeFloatParam(_shader, PropParticleLifetime, lifetime);
            _cmd.SetComputeFloatParam(_shader, PropParticleEmission, emission);
            _cmd.SetComputeVectorParam(_shader, PropParticleBirth, new Vector4(velocityScale, normalSpeed, MaxInheritedSpeed, 0f));
            _cmd.SetComputeVectorParam(_shader, PropParticleWind, wind);
            _cmd.SetComputeVectorParam(_shader, PropParticleFlow, new Vector4(drag, turbulence, turbulenceFrequency, turbulenceSpeed));
            _cmd.SetComputeVectorParam(_shader, PropParticleShape, new Vector4(size, endSize, sizeVariation, 0f));
            _cmd.SetComputeVectorParam(_shader, PropParticleLook, new Vector4(opacity, sourceColor, sourceLift, 0f));
            _cmd.SetComputeVectorArrayParam(_shader, PropParticleColorOverLife, _colorOverLifeSamples);
            _cmd.SetComputeMatrixParam(_shader, PropParticleWorldToObject, t.worldToLocalMatrix);
            _cmd.SetComputeFloatParam(_shader, PropParticleObjectScale, objectScale);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropSourcePrev, _sourcePrev);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropParticleState, _state);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropParticleBase, _base);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropParticleAnim, _anim);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropParticleVelocity, _velocity);
            _cmd.DispatchCompute(_shader, _kernel, (capacity + GroupSize - 1) / GroupSize, 1, 1);

            source.SetAssetDataOnCS(_cmd, _shader, _kernelStore);
            _cmd.SetComputeBufferParam(_shader, _kernelStore, PropSourcePrev, _sourcePrev);
            _cmd.DispatchCompute(_shader, _kernelStore, (_sourcePrev.count + GroupSize - 1) / GroupSize, 1, 1);
            Graphics.ExecuteCommandBuffer(_cmd);

            _renderer.SetDeformedBase(_base, null);
            _renderer.SetAnimationOutput(_anim, null);
            // A bound on the particle speed, for how far velocity streaks reach
            _renderer.SetVelocity(_velocity, MaxInheritedSpeed + normalSpeed + wind.magnitude + turbulence * 2f);
        }

        private bool CanEmit()
        {
            if (_shader == null || _kernel < 0 || _kernelStore < 0 || _renderer == null || source == null || source == _renderer)
                return false;
            return source.isActiveAndEnabled && source.HasValidAsset && source.HasValidRenderSetup;
        }

        private void Stop()
        {
            if (_renderer == null)
                return;
            _renderer.StylerOverride = null;
            _renderer.ClearDeformedBase();
            _renderer.ClearAnimationOutput();
            _renderer.ClearVelocity();
        }

        private void EnsureResources()
        {
            EnsureParticles();
            // The previous source pose starts unwritten (w = 0), so the first
            // births after a change inherit no velocity
            int sourceCount = Mathf.Max(1, source.EffectiveSplatCount);
            if (_sourcePrev == null || _sourcePrev.count != sourceCount)
            {
                _sourcePrev?.Dispose();
                _sourcePrev = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sourceCount, 16) { name = "GaussianParticleSourcePrev" };
                _sourcePrev.SetData(new Vector4[sourceCount]);
            }
        }

        private void EnsureParticles()
        {
            if (_template != null && _template.splatCount == capacity && _renderer.splatAsset == _template)
                return;
            ReleaseResources();

            _cmd = new CommandBuffer { name = "GaussianParticles" };

            _template = CreateTemplate(capacity, _templateData);
            _renderer.splatAsset = _template;
            _renderer.nextAsset = _template;

            _state = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * 3, 16) { name = "GaussianParticleState" };
            // Life index -1 matches no life, so every particle is born on the first update
            var unborn = new Vector4[capacity * 3];
            for (int i = 0; i < capacity; i++)
                unborn[i * 3] = new Vector4(0f, 0f, 0f, -1f);
            _state.SetData(unborn);
            _base = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * 2, 16) { name = "GaussianParticleBase" };
            _anim = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * 3, 16) { name = "GaussianParticleAnim" };
            _velocity = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16) { name = "GaussianParticleVelocity" };
        }

        private void ReleaseResources()
        {
            Stop();
            _state?.Dispose();
            _state = null;
            _sourcePrev?.Dispose();
            _sourcePrev = null;
            _base?.Dispose();
            _base = null;
            _anim?.Dispose();
            _anim = null;
            _velocity?.Dispose();
            _velocity = null;
            if (_renderer != null && _renderer.splatAsset == _template)
            {
                _renderer.splatAsset = null;
                _renderer.nextAsset = null;
            }
            DestroyGenerated(_template);
            _template = null;
            foreach (TextAsset data in _templateData)
                DestroyGenerated(data);
            _templateData.Clear();
            _cmd?.Release();
            _cmd = null;
        }

        private static void DestroyGenerated(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                Destroy(obj);
            else
                DestroyImmediate(obj);
        }

        // An asset of identical tiny splats: white, opaque, round, at the
        // origin. The particle buffers replace their pose, size, opacity and
        // color, so the asset only gives the renderer its splat count.
        private static GaussianSplatAsset CreateTemplate(int count, List<TextAsset> data)
        {
            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = "GaussianParticles";
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.Initialize(count, GaussianSplatAsset.VectorFormat.Float32, GaussianSplatAsset.VectorFormat.Float32,
                GaussianSplatAsset.ColorFormat.Norm8x4, GaussianSplatAsset.SHFormat.Float32, 0,
                Vector3.one * -1000f, Vector3.one * 1000f, null);

            var pos = new byte[GaussianSplatAsset.CalcPosDataSize(count, GaussianSplatAsset.VectorFormat.Float32)];

            // Rotation (10.10.10.2 smallest three of the identity), then scale
            int otherStride = GaussianSplatAsset.GetOtherSize(GaussianSplatAsset.VectorFormat.Float32, GaussianSplatAsset.SHFormat.Float32);
            var other = new byte[(long)count * otherStride];
            uint rotation = 512u | (512u << 10) | (512u << 20) | (3u << 30);
            byte[] rotationBytes = BitConverter.GetBytes(rotation);
            byte[] scale = BitConverter.GetBytes(TemplateScale);
            for (int i = 0; i < count; i++)
            {
                int o = i * otherStride;
                Buffer.BlockCopy(rotationBytes, 0, other, o, 4);
                Buffer.BlockCopy(scale, 0, other, o + 4, 4);
                Buffer.BlockCopy(scale, 0, other, o + 8, 4);
                Buffer.BlockCopy(scale, 0, other, o + 12, 4);
            }

            var color = new byte[GaussianSplatAsset.CalcColorDataSize(count, GaussianSplatAsset.ColorFormat.Norm8x4)];
            Array.Fill(color, (byte)255);

            TextAsset posData = CreateData("Pos", pos, data);
            TextAsset otherData = CreateData("Other", other, data);
            TextAsset colorData = CreateData("Color", color, data);
            asset.SetAssetFiles(null, posData, otherData, colorData, null);
            asset.SetDataHash(Hash128.Compute(count));
            return asset;
        }

        private static TextAsset CreateData(string label, byte[] bytes, List<TextAsset> data)
        {
            var text = new TextAsset(new ReadOnlySpan<byte>(bytes))
            {
                name = "GaussianParticles" + label,
                hideFlags = HideFlags.HideAndDontSave,
            };
            data.Add(text);
            return text;
        }

        int IGaussianTimelineTarget.ChannelCount => TimelineChannels.Length;

        string IGaussianTimelineTarget.GetChannelName(int index) => TimelineChannels[index];

        float IGaussianTimelineTarget.GetChannelValue(int index) => index switch
        {
            0 => emission,
            1 => opacity,
            _ => turbulence,
        };

        void IGaussianTimelineTarget.SetChannelValue(int index, float value)
        {
            switch (index)
            {
                case 0:
                    emission = Mathf.Clamp01(value);
                    break;
                case 1:
                    opacity = Mathf.Max(0f, value);
                    break;
                default:
                    turbulence = Mathf.Max(0f, value);
                    break;
            }
        }

        float IGaussianTimelineTarget.BlendChannel(int index, float from, float to, float t) => Mathf.Lerp(from, to, t);
    }
}
