using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Lines between nearby particles of a GaussianParticleEmitter, the
    /// emitter on the renderer right above this GameObject: each particle
    /// links to its nearest particles within Max Distance, and a link fades
    /// out as its ends drift apart, gone at that distance, and fades with
    /// either end. The links are this GameObject's own splats, one per link,
    /// which a style with Segment streaks draws as lines from one end to the
    /// other; sorting, ODS capture and depth of field treat them like any
    /// other splats. They are rebuilt each time the renderer is drawn, after
    /// the emitter has simulated.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Links/Particle Links")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianParticleLinks : MonoBehaviour, ITideChannelTarget
    {
        /// <summary>Most links a particle can have, as the shader takes them.</summary>
        // Must match MAX_LINKS_PER_POINT in GaussianParticleLinks.compute
        public const int MaxLinksPerPoint = 8;

        // Must match BUILD_GROUP_SIZE in GaussianParticleLinks.compute
        private const int BuildGroupSize = 64;
        // Splat scale of the template's splats, and the world size of a link
        // splat; the style's streak width sets how wide the line is drawn
        private const float TemplateScale = 0.001f;
        private const float LinkWorldSize = 0.0005f;

        private static readonly string[] TimelineChannels = { "Opacity", "Max Distance" };

        private static readonly int PropLinksPerPoint = Shader.PropertyToID("_LinksPerPoint");
        private static readonly int PropLinkParams = Shader.PropertyToID("_LinkParams");
        private static readonly int PropLinkColor = Shader.PropertyToID("_LinkColor");
        private static readonly int PropLinkWorldToObject = Shader.PropertyToID("_LinkWorldToObject");
        private static readonly int PropLinkSplatScale = Shader.PropertyToID("_LinkSplatScale");
        private static readonly int PropLinkBase = Shader.PropertyToID("_LinkBase");
        private static readonly int PropLinkAnim = Shader.PropertyToID("_LinkAnim");
        private static readonly int PropLinkSegments = Shader.PropertyToID("_LinkSegments");

        [Tooltip("World distance at which a link breaks; it fades in as its ends come closer")]
        [SerializeField, Min(0.001f)] private float maxDistance = 0.04f;
        [Tooltip("Most links a particle makes, to its nearest particles. Each particle holds this many link splats")]
        [SerializeField, Range(1, MaxLinksPerPoint)] private int linksPerPoint = 3;
        [Tooltip("How a link fades with the distance between its ends: 1 fades it evenly up to Max Distance, higher keeps only short links bright")]
        [SerializeField, Range(0f, 8f)] private float fadePower = 1f;
        [Tooltip("Multiplies the opacity of every link")]
        [SerializeField, Range(0f, 4f)] private float opacity = 1f;
        [Tooltip("Link color (above 1 glows with bloom), times the colors of its ends as weighted by Point Tint; the link's own styler styles the result")]
        [SerializeField, ColorUsage(false, true)] private Color color = Color.white;
        [Tooltip("How much a link takes the colors of the particles it joins (their mean, before their style): 0 ignores them, 1 multiplies by them")]
        [SerializeField, Range(0f, 1f)] private float pointTint = 1f;

        private GaussianSplatRenderer _renderer;
        private ComputeShader _shader;
        private int _kernelBuild = -1;
        private GaussianPointHash _hash;
        private Action<CommandBuffer, int> _bindParticles;
        private GaussianParticleEmitter _emitter;
        private CommandBuffer _cmd;

        private GaussianSplatAsset _template;
        private readonly List<TextAsset> _templateData = new();
        private GraphicsBuffer _base;
        private GraphicsBuffer _anim;
        private GraphicsBuffer _segments;

        /// <summary>The emitter whose particles are linked: on the renderer right above this GameObject.</summary>
        public GaussianParticleEmitter Emitter
        {
            get
            {
                GaussianSplatRenderer parent = GaussianHierarchy.ParentRenderer(this);
                return parent != null ? parent.GetComponent<GaussianParticleEmitter>() : null;
            }
        }

        public float MaxDistance
        {
            get => maxDistance;
            set => maxDistance = Mathf.Max(0.001f, value);
        }

        public int LinksPerPoint => linksPerPoint;

        public float Opacity
        {
            get => opacity;
            set => opacity = Mathf.Max(0f, value);
        }

        private void OnEnable()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            _shader = Resources.Load<ComputeShader>("GaussianParticleLinks");
            if (_shader != null)
            {
                _kernelBuild = _shader.FindKernel("CSLinkBuild");
                _hash = new GaussianPointHash(_shader);
            }
            _bindParticles = BindParticles;
            _renderer.BeforeGather += Build;
        }

        private void OnDisable()
        {
            _renderer.BeforeGather -= Build;
            ReleaseResources();
        }

        // Links the emitter's particles as they are now
        private void Build(Camera cam)
        {
            _emitter = Emitter;
            if (_shader == null || _kernelBuild < 0 || _emitter == null || !_emitter.isActiveAndEnabled || !_emitter.HasParticles)
            {
                ReleaseResources();
                return;
            }
            int particles = _emitter.Capacity;
            int k = Mathf.Clamp(linksPerPoint, 1, MaxLinksPerPoint);
            EnsureResources(particles, k);

            Vector3 scale = transform.lossyScale;
            float objectScale = Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Max(Mathf.Abs(scale.z), 1e-6f));

            _cmd.Clear();
            _hash.Build(_cmd, maxDistance, _bindParticles);
            _cmd.SetComputeIntParam(_shader, PropLinksPerPoint, k);
            _cmd.SetComputeVectorParam(_shader, PropLinkParams, new Vector4(maxDistance, fadePower, opacity, pointTint));
            _cmd.SetComputeVectorParam(_shader, PropLinkColor, color);
            _cmd.SetComputeMatrixParam(_shader, PropLinkWorldToObject, transform.worldToLocalMatrix);
            _cmd.SetComputeFloatParam(_shader, PropLinkSplatScale, LinkWorldSize / TemplateScale / objectScale);

            int particleGroups = (particles + BuildGroupSize - 1) / BuildGroupSize;
            BindParticles(_cmd, _kernelBuild);
            _hash.SetOnCS(_cmd, _kernelBuild);
            _cmd.SetComputeBufferParam(_shader, _kernelBuild, PropLinkBase, _base);
            _cmd.SetComputeBufferParam(_shader, _kernelBuild, PropLinkAnim, _anim);
            _cmd.SetComputeBufferParam(_shader, _kernelBuild, PropLinkSegments, _segments);
            _cmd.DispatchCompute(_shader, _kernelBuild, particleGroups, 1, 1);
            Graphics.ExecuteCommandBuffer(_cmd);

            _renderer.SetDeformedBase(_base, null);
            _renderer.SetAnimationOutput(_anim, null);
            _renderer.SetSegments(_segments, maxDistance);
        }

        private void BindParticles(CommandBuffer cmb, int kernelIndex)
        {
            _emitter.SetParticlesOnCS(cmb, _shader, kernelIndex);
        }

        private void EnsureResources(int particles, int k)
        {
            int links = particles * k;
            if (_template != null && _template.splatCount == links && _renderer.splatAsset == _template &&
                _hash.PointCount == particles)
                return;
            ReleaseResources();

            _cmd = new CommandBuffer { name = "GaussianParticleLinks" };

            _template = GaussianSplatTemplate.Create("GaussianParticleLinks", links, TemplateScale, _templateData);
            _renderer.splatAsset = _template;
            _renderer.nextAsset = _template;

            _hash.Ensure(particles);
            _base = new GraphicsBuffer(GraphicsBuffer.Target.Structured, links * 2, 16) { name = "GaussianLinkBase" };
            _anim = new GraphicsBuffer(GraphicsBuffer.Target.Structured, links * 3, 16) { name = "GaussianLinkAnim" };
            _segments = new GraphicsBuffer(GraphicsBuffer.Target.Structured, links, 16) { name = "GaussianLinkSegments" };
        }

        private void ReleaseResources()
        {
            if (_renderer != null)
            {
                _renderer.ClearDeformedBase();
                _renderer.ClearAnimationOutput();
                _renderer.ClearSegments();
                if (_renderer.splatAsset == _template)
                {
                    _renderer.splatAsset = null;
                    _renderer.nextAsset = null;
                }
            }
            _hash?.Dispose();
            _base?.Dispose();
            _base = null;
            _anim?.Dispose();
            _anim = null;
            _segments?.Dispose();
            _segments = null;
            GaussianSplatTemplate.Destroy(_template, _templateData);
            _template = null;
            _cmd?.Release();
            _cmd = null;
        }

        int ITideChannelTarget.ChannelCount => TimelineChannels.Length;

        string ITideChannelTarget.GetChannelName(int index) => TimelineChannels[index];

        TideValueType ITideChannelTarget.GetChannelType(int index) => TideValueType.Float;

        TideValue ITideChannelTarget.GetChannelValue(int index) => index == 0 ? opacity : maxDistance;

        void ITideChannelTarget.SetChannelValue(int index, TideValue value)
        {
            if (index == 0)
                Opacity = value.AsFloat;
            else
                MaxDistance = value.AsFloat;
        }
    }
}
