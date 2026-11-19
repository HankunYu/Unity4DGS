using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// A lattice of points fixed in space that links to a target renderer's
    /// splats as it passes: each lattice point near the target draws a line
    /// to its nearest target splats, and the line fades as the target moves
    /// away and breaks at Max Distance. From outside a surface the nearest
    /// splat lies along its normal, so the lines stand out of the body like
    /// pins, brightest at the lattice end. Lattice points a little farther
    /// out can show as faint dots (Reveal). The lattice fills a box of Size
    /// around this GameObject, turned with it.
    ///
    /// The lines are this GameObject's own splats, drawn by a style with
    /// Segment streaks, so sorting, ODS capture and depth of field treat them
    /// like any other splats. They are rebuilt each time the renderer is
    /// drawn, after the target is posed, from a sample of the target's
    /// splats as it draws them.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Links/Grid Links")]
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianGridLinks : MonoBehaviour, ITideChannelTarget
    {
        /// <summary>Most links a lattice point can have, as the shader takes them.</summary>
        // Must match MAX_LINKS_PER_POINT in GaussianGridLinks.compute
        public const int MaxLinksPerPoint = 4;

        // Must match BUILD_GROUPS in GaussianGridLinks.compute
        private const int BuildGroups = 4096;
        private const int GroupSize = 256;
        private const int StateCount = 7;
        // Splat scale of the template's splats, and the world size of a link
        // splat; the style's streak width sets how wide the line is drawn
        private const float TemplateScale = 0.001f;
        private const float LinkWorldSize = 0.0005f;

        private static readonly string[] TimelineChannels = { "Opacity", "Max Distance", "Reveal Distance" };

        private static readonly int PropGridSampleCount = Shader.PropertyToID("_GridSampleCount");
        private static readonly int PropGridSamples = Shader.PropertyToID("_GridSamples");
        private static readonly int PropGridPoints = Shader.PropertyToID("_GridPoints");
        private static readonly int PropGridPointsRead = Shader.PropertyToID("_GridPointsRead");
        private static readonly int PropGridState = Shader.PropertyToID("_GridState");
        private static readonly int PropGridWorldToLattice = Shader.PropertyToID("_GridWorldToLattice");
        private static readonly int PropGridLatticeToWorld = Shader.PropertyToID("_GridLatticeToWorld");
        private static readonly int PropGridInvSpacing = Shader.PropertyToID("_GridInvSpacing");
        private static readonly int PropGridExtent = Shader.PropertyToID("_GridExtent");
        private static readonly int PropGridParams = Shader.PropertyToID("_GridParams");
        private static readonly int PropGridLinkParams = Shader.PropertyToID("_GridLinkParams");
        private static readonly int PropGridRevealParams = Shader.PropertyToID("_GridRevealParams");
        private static readonly int PropGridColor = Shader.PropertyToID("_GridColor");
        private static readonly int PropGridCapacity = Shader.PropertyToID("_GridCapacity");
        private static readonly int PropGridLinkWorldToObject = Shader.PropertyToID("_GridLinkWorldToObject");
        private static readonly int PropGridSplatScale = Shader.PropertyToID("_GridSplatScale");
        private static readonly int PropGridLinkBase = Shader.PropertyToID("_GridLinkBase");
        private static readonly int PropGridLinkAnim = Shader.PropertyToID("_GridLinkAnim");
        private static readonly int PropGridLinkSegments = Shader.PropertyToID("_GridLinkSegments");
        private static readonly int PropSourceObjectToWorld = Shader.PropertyToID("_SourceObjectToWorld");
        private static readonly int PropMatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
        private static readonly int PropCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");

        [Header("Lattice")]
        [Tooltip("World size of the box the lattice fills, centered on this GameObject and turned with it; its scale is ignored")]
        [SerializeField] private Vector3 size = new(4f, 2.5f, 4f);
        [Tooltip("World distance between neighbouring lattice points")]
        [SerializeField, Min(0.005f)] private float spacing = 0.03f;
        [Tooltip("How far each lattice point is moved off its place at random, as a fraction of the spacing; breaks up the regular rows")]
        [SerializeField, Range(0f, 1f)] private float jitter = 0.5f;
        [Tooltip("Varies the jitter")]
        [SerializeField] private int seed;

        [Header("Target")]
        [Tooltip("The renderer whose splats the lattice links to, as it is drawn (pose, style and lighting)")]
        [SerializeField] private GaussianSplatRenderer target;
        [Tooltip("Takes every Nth splat of the target: fewer splats cost less, and a lattice point still finds one close to the surface")]
        [SerializeField, Range(1, 256)] private int sampleStep = 16;
        [Tooltip("Meshes of the target's GaussianMeshDriver binding whose splats are left out, by their path in the binding (e.g. a carried bag)")]
        [SerializeField] private string[] excludeMeshes = Array.Empty<string>();
        [Tooltip("Link only lattice points outside the target, in front of the surface nearest them")]
        [SerializeField] private bool outsideOnly = true;

        [Header("Links")]
        [Tooltip("World distance at which a link breaks; it fades in as the target comes closer")]
        [SerializeField, Min(0.001f)] private float maxDistance = 0.05f;
        [Tooltip("Links per lattice point, to its nearest target splats")]
        [SerializeField, Range(1, MaxLinksPerPoint)] private int linksPerPoint = 1;
        [Tooltip("How a link fades with its length: 1 fades it evenly up to Max Distance, higher keeps only short links bright")]
        [SerializeField, Range(0f, 8f)] private float fadePower = 1f;
        [Tooltip("Multiplies the opacity of every link")]
        [SerializeField, Range(0f, 4f)] private float opacity = 1f;
        [Tooltip("Link and dot color (above 1 glows with bloom), times the color of the target splat as weighted by Point Tint; the link's own styler styles the result")]
        [SerializeField, ColorUsage(false, true)] private Color color = Color.white;
        [Tooltip("How much a link takes the color of the target splat it reaches: 0 ignores it, 1 multiplies by it")]
        [SerializeField, Range(0f, 1f)] private float pointTint = 0.5f;
        [Tooltip("Most link splats and dots drawn at once; more are dropped")]
        [SerializeField, Min(1)] private int capacity = 30000;

        [Header("Reveal")]
        [Tooltip("Lattice points between Max Distance and this distance from the target show as dots, fading out towards it; 0 shows none")]
        [SerializeField, Min(0f)] private float revealDistance;
        [Tooltip("Opacity of a revealed dot next to the links")]
        [SerializeField, Range(0f, 4f)] private float revealOpacity = 0.3f;

        private GaussianSplatRenderer _renderer;
        private ComputeShader _shader;
        private int _kernelReset = -1;
        private int _kernelGather = -1;
        private int _kernelClearLinks = -1;
        private int _kernelBuild = -1;
        private GaussianPointHash _hash;
        private Action<CommandBuffer, int> _bindPoints;
        private CommandBuffer _cmd;

        private GaussianSplatAsset _template;
        private readonly List<TextAsset> _templateData = new();
        private GraphicsBuffer _samples;
        private GraphicsBuffer _points;
        private GraphicsBuffer _state;
        private GraphicsBuffer _base;
        private GraphicsBuffer _anim;
        private GraphicsBuffer _segments;

        // What the sample list was built from
        private GaussianSplatRenderer _samplesTarget;
        private GaussianMeshBinding _samplesBinding;
        private int _samplesSplatCount = -1;
        private int _samplesStep;
        private string _samplesExcluded;
        private readonly List<uint> _sampleList = new();

        public GaussianSplatRenderer Target
        {
            get => target;
            set => target = value;
        }

        public float MaxDistance
        {
            get => maxDistance;
            set => maxDistance = Mathf.Max(0.001f, value);
        }

        public float RevealDistance
        {
            get => revealDistance;
            set => revealDistance = Mathf.Max(0f, value);
        }

        public float Opacity
        {
            get => opacity;
            set => opacity = Mathf.Max(0f, value);
        }

        public Vector3 Size => size;
        public float Spacing => spacing;
        public int Capacity => capacity;

        /// <summary>Lattice points in the box.</summary>
        public long LatticePointCount
        {
            get
            {
                Vector3Int extent = Extent;
                return (2L * extent.x + 1) * (2L * extent.y + 1) * (2L * extent.z + 1);
            }
        }

        /// <summary>Target splats sampled at the last build.</summary>
        public int SampleCount => _samples != null ? _sampleList.Count : 0;

        // Lattice points span -extent .. +extent on each axis
        private Vector3Int Extent => new(
            Mathf.FloorToInt(size.x * 0.5f / spacing),
            Mathf.FloorToInt(size.y * 0.5f / spacing),
            Mathf.FloorToInt(size.z * 0.5f / spacing));

        private void OnEnable()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            _shader = Resources.Load<ComputeShader>("GaussianGridLinks");
            if (_shader != null)
            {
                _kernelReset = _shader.FindKernel("CSGridReset");
                _kernelGather = _shader.FindKernel("CSGridGather");
                _kernelClearLinks = _shader.FindKernel("CSGridClearLinks");
                _kernelBuild = _shader.FindKernel("CSGridBuild");
                _hash = new GaussianPointHash(_shader);
            }
            _bindPoints = BindPoints;
            _renderer.BeforeGather += Build;
        }

        private void OnDisable()
        {
            _renderer.BeforeGather -= Build;
            ReleaseResources();
        }

        private void OnValidate()
        {
            size = Vector3.Max(size, Vector3.zero);
            capacity = Mathf.Max(1, capacity);
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.4f, 0.8f, 1f, 0.6f);
            Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
            Gizmos.DrawWireCube(Vector3.zero, size);
        }

        // Links the lattice to the target as it is drawn now
        private void Build(Camera cam)
        {
            if (_shader == null || _kernelBuild < 0 || target == null || target == _renderer || !target.isActiveAndEnabled ||
                !target.HasValidAsset || !target.HasValidRenderSetup)
            {
                ReleaseResources();
                return;
            }
            EnsureResources();
            if (_sampleList.Count == 0)
                return;

            Transform t = transform;
            Matrix4x4 latticeToWorld = Matrix4x4.TRS(t.position, t.rotation, Vector3.one) * Matrix4x4.Scale(Vector3.one * spacing);
            Vector3 scale = t.lossyScale;
            float objectScale = Mathf.Max(Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y)), Mathf.Max(Mathf.Abs(scale.z), 1e-6f));
            Vector3Int extent = Extent;
            int k = Mathf.Clamp(linksPerPoint, 1, MaxLinksPerPoint);
            float reach = Mathf.Max(maxDistance, revealDistance);

            _cmd.Clear();
            _cmd.SetComputeIntParam(_shader, PropGridSampleCount, _sampleList.Count);
            _cmd.SetComputeMatrixParam(_shader, PropGridWorldToLattice, latticeToWorld.inverse);
            _cmd.SetComputeMatrixParam(_shader, PropGridLatticeToWorld, latticeToWorld);
            _cmd.SetComputeFloatParam(_shader, PropGridInvSpacing, 1f / spacing);
            _cmd.SetComputeIntParams(_shader, PropGridExtent, extent.x, extent.y, extent.z);
            _cmd.SetComputeVectorParam(_shader, PropGridParams, new Vector4(jitter, seed, outsideOnly ? 1f : 0f, k));
            _cmd.SetComputeVectorParam(_shader, PropGridLinkParams, new Vector4(maxDistance, fadePower, opacity, pointTint));
            _cmd.SetComputeVectorParam(_shader, PropGridRevealParams, new Vector4(revealDistance, revealOpacity, 0f, 0f));
            _cmd.SetComputeVectorParam(_shader, PropGridColor, color);
            _cmd.SetComputeIntParam(_shader, PropGridCapacity, capacity);
            _cmd.SetComputeMatrixParam(_shader, PropGridLinkWorldToObject, t.worldToLocalMatrix);
            _cmd.SetComputeFloatParam(_shader, PropGridSplatScale, LinkWorldSize / TemplateScale / objectScale);

            _cmd.SetComputeBufferParam(_shader, _kernelReset, PropGridState, _state);
            _cmd.DispatchCompute(_shader, _kernelReset, 1, 1, 1);

            // The target as it is drawn: its view-dependent style terms are
            // judged from this camera
            target.RefreshStyle();
            target.RefreshLighting();
            target.SetAssetDataOnCS(_cmd, _shader, _kernelGather);
            target.SetStyleOnCS(_cmd, _shader, _kernelGather);
            target.SetLightingOnCS(_cmd, _shader, _kernelGather);
            _cmd.SetComputeMatrixParam(_shader, PropSourceObjectToWorld, target.transform.localToWorldMatrix);
            _cmd.SetComputeMatrixParam(_shader, PropMatrixWorldToObject, target.transform.worldToLocalMatrix);
            _cmd.SetComputeVectorParam(_shader, PropCameraPos, cam != null ? cam.transform.position : target.transform.position);
            _cmd.SetComputeBufferParam(_shader, _kernelGather, PropGridSamples, _samples);
            _cmd.SetComputeBufferParam(_shader, _kernelGather, PropGridPoints, _points);
            _cmd.SetComputeBufferParam(_shader, _kernelGather, PropGridState, _state);
            _cmd.DispatchCompute(_shader, _kernelGather, (_sampleList.Count + GroupSize - 1) / GroupSize, 1, 1);

            _hash.Build(_cmd, reach, _bindPoints);

            _cmd.SetComputeBufferParam(_shader, _kernelClearLinks, PropGridLinkBase, _base);
            _cmd.SetComputeBufferParam(_shader, _kernelClearLinks, PropGridLinkAnim, _anim);
            _cmd.SetComputeBufferParam(_shader, _kernelClearLinks, PropGridLinkSegments, _segments);
            _cmd.DispatchCompute(_shader, _kernelClearLinks, (capacity + GroupSize - 1) / GroupSize, 1, 1);

            BindPoints(_cmd, _kernelBuild);
            _hash.SetOnCS(_cmd, _kernelBuild);
            _cmd.SetComputeBufferParam(_shader, _kernelBuild, PropGridState, _state);
            _cmd.SetComputeBufferParam(_shader, _kernelBuild, PropGridLinkBase, _base);
            _cmd.SetComputeBufferParam(_shader, _kernelBuild, PropGridLinkAnim, _anim);
            _cmd.SetComputeBufferParam(_shader, _kernelBuild, PropGridLinkSegments, _segments);
            _cmd.DispatchCompute(_shader, _kernelBuild, BuildGroups, 1, 1);
            Graphics.ExecuteCommandBuffer(_cmd);

            _renderer.SetDeformedBase(_base, null);
            _renderer.SetAnimationOutput(_anim, null);
            _renderer.SetSegments(_segments, reach);
        }

        // The gathered target splats, for the hash and the build
        private void BindPoints(CommandBuffer cmb, int kernelIndex)
        {
            cmb.SetComputeBufferParam(_shader, kernelIndex, PropGridPointsRead, _points);
        }

        private void EnsureResources()
        {
            if (_template == null || _template.splatCount != capacity || _renderer.splatAsset != _template)
            {
                ReleaseResources();
                _cmd = new CommandBuffer { name = "GaussianGridLinks" };
                _template = GaussianSplatTemplate.Create("GaussianGridLinks", capacity, TemplateScale, _templateData);
                _renderer.splatAsset = _template;
                _renderer.nextAsset = _template;
                _state = new GraphicsBuffer(GraphicsBuffer.Target.Structured, StateCount, 4) { name = "GaussianGridState" };
                _base = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * 2, 16) { name = "GaussianGridLinkBase" };
                _anim = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity * 3, 16) { name = "GaussianGridLinkAnim" };
                _segments = new GraphicsBuffer(GraphicsBuffer.Target.Structured, capacity, 16) { name = "GaussianGridLinkSegments" };
            }
            EnsureSamples();
        }

        // The target splats to gather: every sampleStep-th, leaving out the
        // excluded meshes of the target's binding
        private void EnsureSamples()
        {
            GaussianMeshDriver driver = target.GetComponent<GaussianMeshDriver>();
            GaussianMeshBinding binding = driver != null ? driver.binding : null;
            int splatCount = target.EffectiveSplatCount;
            string excluded = string.Join("\n", excludeMeshes);
            if (_samples != null && _samplesTarget == target && _samplesBinding == binding && _samplesSplatCount == splatCount &&
                _samplesStep == sampleStep && _samplesExcluded == excluded)
                return;

            var skip = new bool[Mathf.Max(splatCount, 0)];
            if (binding != null && binding.meshes != null && excludeMeshes.Length > 0 && binding.entryData != null)
            {
                byte[] entries = binding.entryData.bytes;
                foreach (GaussianMeshBinding.MeshInfo mesh in binding.meshes)
                {
                    if (Array.IndexOf(excludeMeshes, mesh.path) < 0)
                        continue;
                    for (int e = mesh.firstEntry; e < mesh.firstEntry + mesh.entryCount; e++)
                    {
                        uint splat = BitConverter.ToUInt32(entries, e * GaussianMeshBinding.EntrySize);
                        if (splat < skip.Length)
                            skip[splat] = true;
                    }
                }
            }
            _sampleList.Clear();
            int step = Mathf.Max(1, sampleStep);
            for (int i = 0; i < splatCount; i += step)
            {
                if (!skip[i])
                    _sampleList.Add((uint)i);
            }

            _samples?.Dispose();
            _samples = null;
            _points?.Dispose();
            _points = null;
            int count = Mathf.Max(1, _sampleList.Count);
            _samples = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianGridSamples" };
            if (_sampleList.Count > 0)
                _samples.SetData(_sampleList);
            _points = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count * 3, 16) { name = "GaussianGridPoints" };
            _hash.Ensure(count);

            _samplesTarget = target;
            _samplesBinding = binding;
            _samplesSplatCount = splatCount;
            _samplesStep = sampleStep;
            _samplesExcluded = excluded;
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
            _samples?.Dispose();
            _samples = null;
            _points?.Dispose();
            _points = null;
            _state?.Dispose();
            _state = null;
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
            _samplesTarget = null;
        }

        int ITideChannelTarget.ChannelCount => TimelineChannels.Length;

        string ITideChannelTarget.GetChannelName(int index) => TimelineChannels[index];

        TideValueType ITideChannelTarget.GetChannelType(int index) => TideValueType.Float;

        TideValue ITideChannelTarget.GetChannelValue(int index) => index switch
        {
            0 => opacity,
            1 => maxDistance,
            _ => revealDistance,
        };

        void ITideChannelTarget.SetChannelValue(int index, TideValue value)
        {
            switch (index)
            {
                case 0:
                    Opacity = value.AsFloat;
                    break;
                case 1:
                    MaxDistance = value.AsFloat;
                    break;
                default:
                    RevealDistance = value.AsFloat;
                    break;
            }
        }
    }
}
