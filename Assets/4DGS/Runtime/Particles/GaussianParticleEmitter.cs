using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using Tide;

namespace GaussianSplatting.Runtime
{
    // Particles drifting off a splat renderer, the source: the nearest one
    // above this GameObject, so a renderer's emitters sit under it as
    // children, each with its own particle renderer (GaussianHierarchy keeps
    // the source's volumes and the particles' apart). Each particle is born
    // at a random splat of the source (within the Birth GaussianEmitVolumes
    // under the emitter, if any), as it is drawn right now, with the color it
    // is drawn in (style included). It can ride on that splat for part of
    // its life (Attach, or until a Release volume reaches it); it then leaves
    // with the velocity of that spot of the surface plus a push along its
    // normal, so a moving body throws particles off, and is simulated step by
    // step, its velocity settling into a flow of wind plus curl noise. Its
    // color, opacity and size follow curves over the life, and it is born
    // again elsewhere. The particles are this GameObject's own splats: the
    // emitter gives its GaussianSplatRenderer a generated asset of `capacity`
    // splats and drives their pose, size, opacity, color and velocity on the
    // GPU, so sorting, styles (from a GaussianStyler of its own, e.g. with
    // Velocity streaks, or the source's), ODS capture and depth of field
    // treat them like any other splats, and the source is untouched.
    //
    // The simulation steps on a fixed grid of SimulationStep seconds of the
    // emitter's clock, so the same clock times give the same particles at
    // any frame rate. The clock runs by itself, or follows a timeline's
    // GaussianParticleTrack. Moving it back, or far ahead, restarts the
    // particles a full life before the new time and simulates up to it
    // (prewarm), so the effect is already flowing wherever it starts; the
    // source is taken in its current pose throughout the prewarm.
    //
    // Runs after the source's mesh driver and animator in LateUpdate, and on
    // every render in edit mode.
    [AddComponentMenu("Gaussian Splatting/Particles/Particle Emitter")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(100)]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianParticleEmitter : MonoBehaviour, ITideChannelTarget
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
        private static readonly int PropParticleSizeOverLife = Shader.PropertyToID("_ParticleSizeOverLife");
        private static readonly int PropEmitVolumeCount = Shader.PropertyToID("_EmitVolumeCount");
        private static readonly int PropEmitVolumeWorldToLocal = Shader.PropertyToID("_EmitVolumeWorldToLocal");
        private static readonly int PropEmitVolumeParams = Shader.PropertyToID("_EmitVolumeParams");
        private static readonly int PropEmitFiltered = Shader.PropertyToID("_EmitFiltered");
        private static readonly int PropEmitCandidates = Shader.PropertyToID("_EmitCandidates");
        private static readonly int PropEmitBlockCounts = Shader.PropertyToID("_EmitBlockCounts");
        private static readonly int PropEmitBlockOffsets = Shader.PropertyToID("_EmitBlockOffsets");
        private static readonly int PropEmitTotal = Shader.PropertyToID("_EmitTotal");
        private static readonly int PropEmitBlockCount = Shader.PropertyToID("_EmitBlockCount");

        /// <summary>Seconds between simulation steps on the emitter's clock.</summary>
        public const double SimulationStep = 1.0 / 120.0;

        private const int GroupSize = 256;
        // Splat scale of the template's splats; the particle size scales it
        private const float TemplateScale = 0.001f;
        // Longest step the clock takes in edit mode, where renders are irregular
        private const float MaxEditStep = 0.1f;
        // Shortest time the source velocity is measured over, and the fastest
        // a particle inherits: a pose that jumps (a scrubbed timeline, two
        // renders around one update) must not fling particles away
        private const float MinVelocityStep = 1f / 240f;
        private const float MaxInheritedSpeed = 3f;
        // Must match OVER_LIFE_SAMPLES and MAX_EMIT_VOLUMES in GaussianParticles.compute
        private const int OverLifeSamples = 32;
        private const int MaxEmitVolumes = 8;
        // A life index no life has, so a cleared particle is born on its next step
        private const float Unborn = -3.0e38f;

        private static readonly string[] TimelineChannels = { "Emission", "Opacity", "Turbulence" };

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
        [Tooltip("Fraction of its life a particle rides on the splat it was born on before it leaves; a Release GaussianEmitVolume frees it earlier. 0 leaves at once")]
        [SerializeField, Range(0f, 1f)] private float attach;

        [Header("Release")]
        [Tooltip("Fraction of the velocity of the source surface a particle leaves with: a moving body throws particles off and trails them")]
        [SerializeField, Range(0f, 2f)] private float inheritVelocity = 1f;
        [Tooltip("World units per second a particle is pushed off along the surface normal (the splat's local +Z, which mesh splats align with the surface) as it leaves")]
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

        [Header("Size")]
        [Tooltip("Average size of a new particle, as a splat scale (Gaussian sigma) in world units")]
        [SerializeField, Min(0f)] private float size = 0.0015f;
        [Tooltip("0 makes every particle the same size; 1 makes most small and a few up to three times the average")]
        [SerializeField, Range(0f, 1f)] private float sizeVariation = 0.7f;
        [Tooltip("Size over a life as a factor of the particle's own size, from birth on the left to death on the right")]
        [SerializeField] private AnimationCurve sizeOverLife = AnimationCurve.Linear(0f, 1f, 1f, 0.4f);

        [Header("Color")]
        [Tooltip("Multiplies the opacity a particle takes from its source splat")]
        [SerializeField, Range(0f, 4f)] private float opacity = 1f;
        [Tooltip("Color (above 1 glows with bloom) and opacity over a life, from birth on the left to death on the right. The particle's color is this, times its source color as weighted by Source Tint; its own styler styles the result")]
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
        [Tooltip("How much Color Over Life is tinted by the source color, the color the splat a particle was born on is drawn in (style included): 0 ignores it, 1 multiplies by it")]
        [FormerlySerializedAs("sourceColor")]
        [SerializeField, Range(0f, 1f)] private float sourceTint = 1f;
        [Tooltip("Brightens dark source colors before they tint, keeping their hue, so particles off a dark surface still show: 0 takes them as drawn, 1 brings every color up to full brightness")]
        [SerializeField, Range(0f, 1f)] private float sourceLift;

        private GaussianSplatRenderer _renderer;
        private GaussianSplatRenderer _source;
        private ComputeShader _shader;
        private int _kernel = -1;
        private int _kernelStore = -1;
        private int _kernelEmitCount = -1;
        private int _kernelEmitScan = -1;
        private int _kernelEmitWrite = -1;
        private CommandBuffer _cmd;

        private GaussianSplatAsset _template;
        private readonly List<TextAsset> _templateData = new();
        private GraphicsBuffer _state;
        private GraphicsBuffer _sourcePrev;
        private GraphicsBuffer _base;
        private GraphicsBuffer _anim;
        private GraphicsBuffer _velocity;
        private GraphicsBuffer _emitCandidates;
        private GraphicsBuffer _emitBlockCounts;
        private GraphicsBuffer _emitBlockOffsets;
        private GraphicsBuffer _emitTotal;
        private readonly Vector4[] _colorOverLifeSamples = new Vector4[OverLifeSamples];
        private readonly Vector4[] _sizeOverLifeSamples = new Vector4[OverLifeSamples / 4];
        private readonly List<GaussianEmitVolume> _volumes = new();
        private readonly Matrix4x4[] _volumeWorldToLocal = new Matrix4x4[MaxEmitVolumes];
        private readonly Vector4[] _volumeParams = new Vector4[MaxEmitVolumes];

        // The clock, and the grid step the particles have been simulated to
        private double _clock;
        private long _simStep;
        // Clock time of the source pose kept in _sourcePrev
        private double _storeTime;
        private bool _needsRestart = true;
        private bool _driven;
        private double _lastEditTime = -1.0;

        // The renderer the particles are born on: the nearest one above this
        // GameObject (GaussianHierarchy)
        public GaussianSplatRenderer Source => GaussianHierarchy.ParentRenderer(this);

        /// <summary>
        /// Seconds on the emitter's clock, which drives every life. Setting it
        /// moves the particles there on their next update: stepping forward,
        /// or restarting with a prewarm when it goes back or far ahead.
        /// </summary>
        public double Time
        {
            get => _clock;
            set => _clock = value;
        }

        /// <summary>Whether a timeline sets the clock (see <see cref="Drive"/>).</summary>
        public bool IsDriven => _driven;

        /// <summary>Particles born per second at the current emission, on average.</summary>
        public float BirthRate => capacity * emission / lifetime;

        /// <summary>Particles shown at once at the current emission.</summary>
        public float ShownCount => capacity * emission;

        /// <summary>Seconds simulated before the clock when the particles restart: the longest life, so each one is mid-flow.</summary>
        public double PrewarmTime => lifetime * 1.25 + SimulationStep;

        /// <summary>
        /// Sets the clock and stops it running by itself, for a timeline that
        /// drives the emitter; <see cref="StopDriving"/> lets it run on.
        /// </summary>
        public void Drive(double time)
        {
            _driven = true;
            _clock = time;
        }

        public void StopDriving()
        {
            _driven = false;
        }

        private void OnEnable()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            _shader = Resources.Load<ComputeShader>("GaussianParticles");
            if (_shader != null)
            {
                _kernel = _shader.FindKernel("CSUpdateParticles");
                _kernelStore = _shader.FindKernel("CSStoreSource");
                _kernelEmitCount = _shader.FindKernel("CSEmitCount");
                _kernelEmitScan = _shader.FindKernel("CSEmitScan");
                _kernelEmitWrite = _shader.FindKernel("CSEmitWrite");
            }
            _needsRestart = true;
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
            // A driven clock may stand still (a paused timeline): restart so
            // the change shows. A free clock shows it as new lives come in.
            if (_driven)
                _needsRestart = true;
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            if (!_driven)
                _clock += UnityEngine.Time.deltaTime;
            Simulate();
        }

        private void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (Application.isPlaying)
                return;
            double now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (!_driven && _lastEditTime >= 0.0)
                _clock += Math.Min(now - _lastEditTime, MaxEditStep);
            _lastEditTime = now;
            Simulate();
        }

        // Simulates the particles up to the clock. Runs on its own (see
        // above); call it after posing the source at another point.
        public void Simulate()
        {
            _source = Source;
            // Without the particle buffers the template would draw as is:
            // hand the renderer back empty instead
            if (!CanEmit())
            {
                ReleaseResources();
                return;
            }
            EnsureResources();
            _renderer.StylerOverride = followSourceStyle ? _source.Styler : null;

            long targetStep = (long)Math.Floor(_clock / SimulationStep + 1e-6);
            bool restart = _needsRestart || targetStep < _simStep || (targetStep - _simStep) * SimulationStep > PrewarmTime;
            if (restart)
                Restart();
            int steps = (int)(targetStep - _simStep);

            // Births and attached particles measure the source's velocity
            // against its pose at the last store; a restart has none
            float velocityScale = restart ? 0f : inheritVelocity / Mathf.Max((float)(_clock - _storeTime), MinVelocityStep);

            _cmd.Clear();
            SetSourceParams(velocityScale);
            SetEmitVolumes(out bool filtered);
            if (filtered)
                BuildCandidates();
            for (int i = 1; i <= steps; i++)
            {
                _cmd.SetComputeFloatParam(_shader, PropParticleTime, (float)((_simStep + i) * SimulationStep));
                _cmd.DispatchCompute(_shader, _kernel, (capacity + GroupSize - 1) / GroupSize, 1, 1);
            }
            if (steps > 0 || restart)
            {
                _source.SetAssetDataOnCS(_cmd, _shader, _kernelStore);
                _cmd.SetComputeBufferParam(_shader, _kernelStore, PropSourcePrev, _sourcePrev);
                _cmd.DispatchCompute(_shader, _kernelStore, (_sourcePrev.count + GroupSize - 1) / GroupSize, 1, 1);
                _storeTime = _clock;
            }
            Graphics.ExecuteCommandBuffer(_cmd);
            _simStep = targetStep;

            _renderer.SetDeformedBase(_base, null);
            _renderer.SetAnimationOutput(_anim, null);
            // A bound on the particle speed, for how far velocity streaks reach
            _renderer.SetVelocity(_velocity, MaxInheritedSpeed + normalSpeed + wind.magnitude + turbulence * 2f);
        }

        // Clears every particle and the source's previous pose, and starts
        // the simulation a prewarm before the clock
        private void Restart()
        {
            var unborn = new Vector4[capacity * 3];
            for (int i = 0; i < capacity; i++)
                unborn[i * 3] = new Vector4(0f, 0f, 0f, Unborn);
            _state.SetData(unborn);
            _sourcePrev.SetData(new Vector4[_sourcePrev.count]);
            _simStep = (long)Math.Floor((_clock - PrewarmTime) / SimulationStep);
            _storeTime = _clock;
            _needsRestart = false;
        }

        private void SetSourceParams(float velocityScale)
        {
            Transform t = transform;
            Vector3 scale = t.lossyScale;
            float objectScale = 1f / TemplateScale / Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Max(Mathf.Abs(scale.z), 1e-6f));

            for (int i = 0; i < OverLifeSamples; i++)
            {
                float age = i / (OverLifeSamples - 1f);
                Color c = colorOverLife.Evaluate(age);
                _colorOverLifeSamples[i] = new Vector4(c.r, c.g, c.b, c.a);
                _sizeOverLifeSamples[i / 4][i % 4] = sizeOverLife != null ? sizeOverLife.Evaluate(age) : 1f;
            }

            // The source's view-dependent style terms (rim, depth ramp) are
            // judged from the main camera
            Camera cam = Camera.main;
            Vector3 cameraPos = cam != null ? cam.transform.position : _source.transform.position;

            // A hidden source is not drawn, so nothing else refreshes its style
            _source.RefreshStyle();
            _source.SetAssetDataOnCS(_cmd, _shader, _kernel);
            _source.SetStyleColorOnCS(_cmd, _shader, _kernel);
            _cmd.SetComputeMatrixParam(_shader, PropSourceObjectToWorld, _source.transform.localToWorldMatrix);
            _cmd.SetComputeMatrixParam(_shader, PropMatrixWorldToObject, _source.transform.worldToLocalMatrix);
            _cmd.SetComputeVectorParam(_shader, PropCameraPos, cameraPos);
            _cmd.SetComputeIntParam(_shader, PropParticleCount, capacity);
            _cmd.SetComputeIntParam(_shader, PropParticleSeed, seed);
            _cmd.SetComputeFloatParam(_shader, PropParticleDeltaTime, (float)SimulationStep);
            _cmd.SetComputeFloatParam(_shader, PropParticleLifetime, lifetime);
            _cmd.SetComputeFloatParam(_shader, PropParticleEmission, emission);
            _cmd.SetComputeVectorParam(_shader, PropParticleBirth, new Vector4(velocityScale, normalSpeed, MaxInheritedSpeed, attach));
            _cmd.SetComputeVectorParam(_shader, PropParticleWind, wind);
            _cmd.SetComputeVectorParam(_shader, PropParticleFlow, new Vector4(drag, turbulence, turbulenceFrequency, turbulenceSpeed));
            _cmd.SetComputeVectorParam(_shader, PropParticleShape, new Vector4(size, 0f, sizeVariation, 0f));
            _cmd.SetComputeVectorParam(_shader, PropParticleLook, new Vector4(opacity, sourceTint, sourceLift, 0f));
            _cmd.SetComputeVectorArrayParam(_shader, PropParticleColorOverLife, _colorOverLifeSamples);
            _cmd.SetComputeVectorArrayParam(_shader, PropParticleSizeOverLife, _sizeOverLifeSamples);
            _cmd.SetComputeMatrixParam(_shader, PropParticleWorldToObject, t.worldToLocalMatrix);
            _cmd.SetComputeFloatParam(_shader, PropParticleObjectScale, objectScale);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropSourcePrev, _sourcePrev);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropParticleState, _state);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropParticleBase, _base);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropParticleAnim, _anim);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropParticleVelocity, _velocity);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropEmitCandidates, _emitCandidates);
            _cmd.SetComputeBufferParam(_shader, _kernel, PropEmitTotal, _emitTotal);
        }

        // Loads the active emit volumes under the emitter, as many as the
        // shader takes; filtered is set when birth volumes limit the births
        private void SetEmitVolumes(out bool filtered)
        {
            GaussianHierarchy.GetOwnedComponents(this, _volumes);
            int count = 0;
            filtered = false;
            foreach (GaussianEmitVolume volume in _volumes)
            {
                if (count == MaxEmitVolumes)
                    break;
                if (!volume.isActiveAndEnabled)
                    continue;
                _volumeWorldToLocal[count] = volume.transform.worldToLocalMatrix;
                _volumeParams[count] = new Vector4((float)volume.shape, volume.falloff, volume.weight, (float)volume.role);
                filtered |= volume.role == GaussianEmitVolumeRole.Birth;
                count++;
            }
            _cmd.SetComputeIntParam(_shader, PropEmitVolumeCount, count);
            _cmd.SetComputeMatrixArrayParam(_shader, PropEmitVolumeWorldToLocal, _volumeWorldToLocal);
            _cmd.SetComputeVectorArrayParam(_shader, PropEmitVolumeParams, _volumeParams);
            _cmd.SetComputeIntParam(_shader, PropEmitFiltered, filtered ? 1 : 0);
        }

        // Lists the source splats the birth volumes reach, in source order
        private void BuildCandidates()
        {
            int blocks = _emitBlockCounts.count;
            _cmd.SetComputeIntParam(_shader, PropEmitBlockCount, blocks);
            _source.SetAssetDataOnCS(_cmd, _shader, _kernelEmitCount);
            _cmd.SetComputeBufferParam(_shader, _kernelEmitCount, PropEmitBlockCounts, _emitBlockCounts);
            _cmd.DispatchCompute(_shader, _kernelEmitCount, blocks, 1, 1);
            _cmd.SetComputeBufferParam(_shader, _kernelEmitScan, PropEmitBlockCounts, _emitBlockCounts);
            _cmd.SetComputeBufferParam(_shader, _kernelEmitScan, PropEmitBlockOffsets, _emitBlockOffsets);
            _cmd.SetComputeBufferParam(_shader, _kernelEmitScan, PropEmitTotal, _emitTotal);
            _cmd.DispatchCompute(_shader, _kernelEmitScan, 1, 1, 1);
            _source.SetAssetDataOnCS(_cmd, _shader, _kernelEmitWrite);
            _cmd.SetComputeBufferParam(_shader, _kernelEmitWrite, PropEmitBlockOffsets, _emitBlockOffsets);
            _cmd.SetComputeBufferParam(_shader, _kernelEmitWrite, PropEmitCandidates, _emitCandidates);
            _cmd.DispatchCompute(_shader, _kernelEmitWrite, blocks, 1, 1);
        }

        private bool CanEmit()
        {
            if (_shader == null || _kernel < 0 || _kernelStore < 0 || _kernelEmitCount < 0 || _kernelEmitScan < 0 ||
                _kernelEmitWrite < 0 || _renderer == null || _source == null)
                return false;
            return _source.isActiveAndEnabled && _source.HasValidAsset && _source.HasValidRenderSetup;
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
            int sourceCount = Mathf.Max(1, _source.EffectiveSplatCount);
            if (_sourcePrev == null || _sourcePrev.count != sourceCount)
            {
                ReleaseSourceBuffers();
                _sourcePrev = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sourceCount, 16) { name = "GaussianParticleSourcePrev" };
                _emitCandidates = new GraphicsBuffer(GraphicsBuffer.Target.Structured, sourceCount, 8) { name = "GaussianParticleEmitCandidates" };
                int blocks = (sourceCount + GroupSize - 1) / GroupSize;
                _emitBlockCounts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, blocks, 4) { name = "GaussianParticleEmitBlockCounts" };
                _emitBlockOffsets = new GraphicsBuffer(GraphicsBuffer.Target.Structured, blocks, 4) { name = "GaussianParticleEmitBlockOffsets" };
                _emitTotal = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "GaussianParticleEmitTotal" };
                _emitTotal.SetData(new uint[1]);
                _needsRestart = true;
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
            _base = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * 2, 16) { name = "GaussianParticleBase" };
            _anim = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * 3, 16) { name = "GaussianParticleAnim" };
            _velocity = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16) { name = "GaussianParticleVelocity" };
            _needsRestart = true;
        }

        private void ReleaseSourceBuffers()
        {
            _sourcePrev?.Dispose();
            _sourcePrev = null;
            _emitCandidates?.Dispose();
            _emitCandidates = null;
            _emitBlockCounts?.Dispose();
            _emitBlockCounts = null;
            _emitBlockOffsets?.Dispose();
            _emitBlockOffsets = null;
            _emitTotal?.Dispose();
            _emitTotal = null;
        }

        private void ReleaseResources()
        {
            Stop();
            _state?.Dispose();
            _state = null;
            ReleaseSourceBuffers();
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
            _needsRestart = true;
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

        int ITideChannelTarget.ChannelCount => TimelineChannels.Length;

        string ITideChannelTarget.GetChannelName(int index) => TimelineChannels[index];

        TideValueType ITideChannelTarget.GetChannelType(int index) => TideValueType.Float;

        TideValue ITideChannelTarget.GetChannelValue(int index) => index switch
        {
            0 => emission,
            1 => opacity,
            _ => turbulence,
        };

        void ITideChannelTarget.SetChannelValue(int index, TideValue channelValue)
        {
            float value = channelValue.AsFloat;
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
    }
}
