using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Serialization;
using UnityEngine.XR;

namespace GaussianSplatting.Runtime
{
    [AddComponentMenu("Gaussian Splatting/Splat Renderer")]
    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour, IGaussianTimelineTarget
    {
        [FormerlySerializedAs("m_Asset")]
        public GaussianSplatAsset splatAsset;
        [FormerlySerializedAs("m_NextAsset")]
        public GaussianSplatAsset nextAsset;

        [Tooltip("Draw the splats. Off, everything else keeps running (mesh driver, animation, style), so the renderer can still feed a particle emitter that shows it only through its particles")]
        public bool visible = true;
        [Tooltip("Rendering order compared to other splats. Within same order splats are sorted by distance. Higher order splats render 'on top of' lower order splats.")]
        [FormerlySerializedAs("m_RenderOrder")]
        public int renderOrder;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        [FormerlySerializedAs("m_SplatScale")]
        public float splatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        [FormerlySerializedAs("m_OpacityScale")]
        public float opacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        [FormerlySerializedAs("m_SHOrder")]
        public int shOrder = 3;
        // What is actually shaded: also capped by the config and by the SH
        // bands the asset stores
        public int EffectiveSHOrder => Mathf.Min(shOrder,
            Mathf.Min(GaussianSplatRenderSystem.instance.Config?.maxSHOrder ?? 3, splatAsset != null ? splatAsset.shDegree : 0));
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        [FormerlySerializedAs("m_SHOnly")]
        public bool shOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        [FormerlySerializedAs("m_SortNthFrame")]
        public int sortNthFrame = 1;
        [Tooltip("Hide splats facing away from the camera, like mesh backface culling. Needs splats whose local +Z is the outward surface normal, such as ones sampled from a mesh; trained splats have no such orientation")]
        public bool backfaceCulling;
        [Range(0.0f, 45.0f)] [Tooltip("Degrees before edge-on over which back-facing splats fade out instead of popping; 0 cuts them off hard")]
        public float backfaceFadeAngle = 10.0f;
        [Tooltip("Hide splats lying behind a nearer surface of this model, such as a far leg showing through a near one between sparse points. Splats a style makes additive do not count as a surface. Off under depth of field, whose blurred splats do not hide what is behind them")]
        public bool occlusionCulling;
        [Range(0.005f, 0.5f)] [Tooltip("Depth behind the nearest surface that a splat may sit and still be kept (scene units). Larger than the surface's own depth jitter, smaller than the gap to the surface it should hide")]
        public float occlusionTolerance = 0.03f;
        [Range(0.5f, 8.0f)] [Tooltip("Widens each splat's footprint when building the nearest-surface depth, so sparse samples still form a closed surface")]
        public float occlusionFootprintScale = 2.0f;

        // Screen pixels per occlusion grid cell
        const int OcclusionCellSize = 4;

        // Timeline channels; styles have their own on GaussianStyler and its
        // volumes
        private static readonly string[] TimelineChannels = { "Splat Scale", "Opacity Scale" };

        int IGaussianTimelineTarget.ChannelCount => TimelineChannels.Length;

        string IGaussianTimelineTarget.GetChannelName(int index) => TimelineChannels[index];

        float IGaussianTimelineTarget.GetChannelValue(int index) => index == 0 ? splatScale : opacityScale;

        void IGaussianTimelineTarget.SetChannelValue(int index, float value)
        {
            if (index == 0)
                splatScale = Mathf.Max(0f, value);
            else
                opacityScale = Mathf.Max(0f, value);
        }

        float IGaussianTimelineTarget.BlendChannel(int index, float from, float to, float t) => Mathf.Lerp(from, to, t);

        // The styles this renderer draws with, refreshed from its styler
        // before each frame's view data (RefreshStyle)
        private readonly GaussianStyleSet _styleSet = new();

        // Styler whose volumes style this renderer instead of its own; set by
        // GaussianParticleEmitter so particles take their source's look
        [NonSerialized] internal GaussianStyler StylerOverride;

        internal GaussianStyler Styler => StylerOverride != null ? StylerOverride : GetComponent<GaussianStyler>();

        // Loads the styler's volumes and styles as they are now
        internal void RefreshStyle()
        {
            GaussianStyler styler = Styler;
            if (styler != null)
                styler.FillStyleSet(_styleSet);
            else
                _styleSet.Begin(0f);
        }

        // At zero weight the renderer draws the render mode's own look, which
        // the stylized one converges to as the weight goes to zero
        internal bool StyleActive => _styleSet.Active &&
            GaussianSplatRenderSystem.instance.Config?.renderMode is GaussianSplatRenderMode.Splats or GaussianSplatRenderMode.PointCloud;

        private static bool PointCloudMode =>
            GaussianSplatRenderSystem.instance.Config?.renderMode == GaussianSplatRenderMode.PointCloud;

        // Whether the view data holds baked quads for the quad shader
        // (RenderGaussianStylized.shader): always for points, which it draws
        // styled or not, and for styled splats
        internal bool QuadsBaked => StyleActive || PointCloudMode;

        // Fully additive splats blend the same in any order, so they skip the
        // sort
        internal bool StyleOrderIndependent => StyleActive && _styleSet.OrderIndependent;

        // This renderer's entry in a stylized draw starting at the given splat
        internal GaussianSplatStyleRange StyleRange(int start) =>
            StyleActive ? _styleSet.StyleRange(start, true) : new GaussianSplatStyleRange { start = (uint)start, baked = QuadsBaked ? 1u : 0u };

        // Single-entry range table for this renderer's own stylized draw; the
        // global path keeps one per render order group instead
        internal GraphicsBuffer StyleRangeBuffer
        {
            get
            {
                _gpuStyleRange ??= new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, GaussianSplatStyleRange.Stride)
                    { name = "GaussianSplatStyleRange" };
                return _gpuStyleRange;
            }
        }

        public GaussianCutoutManager CutoutManager => GetComponent<GaussianCutoutManager>();

        // Set by GaussianCutoutManager: cutout data for CalcViewData
        private GraphicsBuffer _cutoutBuffer;
        private int _cutoutCount;

        int _splatCount; // initially same as asset splat count, but editing can change this
        private GraphicsBuffer _gpuSortDistances;
        private GraphicsBuffer _gpuSortKeys;
        private GraphicsBuffer _gpuVisibleCounter;
        private GraphicsBuffer _gpuIndirectArgs;
        // Nearest depth per screen cell for occlusion culling; grows to the
        // largest camera that has rendered this splat
        private GraphicsBuffer _gpuOcclusionGrid;
        private GraphicsBuffer _gpuStyleRange;
        internal GraphicsBuffer _gpuPosData;
        internal GraphicsBuffer _gpuOtherData;
        internal GraphicsBuffer _gpuSHData;

        // Set by GaussianAnimator: per-splat animation output (3 float4s per splat)
        private GraphicsBuffer _animOutputBuffer;
        // Set by GaussianAnimator: animated chunk bounds (min, max float4 per chunk)
        private GraphicsBuffer _animChunkBounds;
        // Set by GaussianMeshDriver: per-splat base pose replacing the asset's
        // position and rotation (2 float4s per splat), and its chunk bounds
        private GraphicsBuffer _deformedBase;
        private GraphicsBuffer _deformedChunkBounds;
        private GraphicsBuffer _velocity;
        private float _velocityMaxSpeed;
        // Set by GaussianMorph: pre-blended splat data (4 float4s per splat)
        private GraphicsBuffer _morphedDataBuffer;
        private GraphicsBuffer _morphSHBuffer;
        private int _morphDataValid;
        private int _morphedSplatCount;
        private float _morphWeight;
        internal Texture _gpuColorData;
        private GraphicsBuffer _gpuChunks;
        private bool _gpuChunksValid;
        internal GraphicsBuffer _gpuView;
        private GraphicsBuffer _gpuIndexBuffer;
        // Largest splat count InitResourcesForAssets sized for; 0 when the
        // resources are sized for the current asset alone.
        private int _preallocatedSplatCount;

        private GaussianSplatEditManager _editManager;

        internal GaussianSplatEditManager EditManager
            => _editManager ??= new GaussianSplatEditManager(this);

        private IGpuSorter _sorter;
        private GpuSortArgs _sorterArgs;
        private readonly Dictionary<string, int> _kernelIndexCache = new();

        private static readonly uint[] ZeroCounterData = { 0 };

        private readonly GaussianSplatSortState _sortState = new();
        private int _contentVersion;
        GaussianSplatAsset _prevAsset;
        private bool _setupErrorLogged;
        private int _deferAssetFrames = 0;
        private const int DeferFrames = 1;
        Hash128 _prevHash;
        bool _registered;

        static readonly ProfilerMarker ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        // Forwarding properties: compute shaders come from global Config
        internal ComputeShader csSplatUtilities => GaussianSplatRenderSystem.instance.Config?.CsSplatUtilities;
        internal ComputeShader csSplatSort => GaussianSplatRenderSystem.instance.Config?.CsSplatSort;
        internal ComputeShader csPortableSort => GaussianSplatRenderSystem.instance.Config?.CsPortableSort;

        internal static class Props
        {
            public static readonly int SplatPos = Shader.PropertyToID("_SplatPos");
            public static readonly int SplatOther = Shader.PropertyToID("_SplatOther");
            public static readonly int SplatSH = Shader.PropertyToID("_SplatSH");
            public static readonly int SplatColor = Shader.PropertyToID("_SplatColor");
            public static readonly int SplatSelectedBits = Shader.PropertyToID("_SplatSelectedBits");
            public static readonly int SplatDeletedBits = Shader.PropertyToID("_SplatDeletedBits");
            public static readonly int SplatBitsValid = Shader.PropertyToID("_SplatBitsValid");
            public static readonly int SplatFormat = Shader.PropertyToID("_SplatFormat");
            public static readonly int SplatChunks = Shader.PropertyToID("_SplatChunks");
            public static readonly int SplatChunkCount = Shader.PropertyToID("_SplatChunkCount");
            public static readonly int SplatViewData = Shader.PropertyToID("_SplatViewData");
            public static readonly int OrderBuffer = Shader.PropertyToID("_OrderBuffer");
            public static readonly int SplatScale = Shader.PropertyToID("_SplatScale");
            public static readonly int SplatOpacityScale = Shader.PropertyToID("_SplatOpacityScale");
            public static readonly int SplatSize = Shader.PropertyToID("_SplatSize");
            public static readonly int PointSizeScale = Shader.PropertyToID("_PointSizeScale");
            public static readonly int PointMinSize = Shader.PropertyToID("_PointMinSize");
            public static readonly int PointMinWorldSize = Shader.PropertyToID("_PointMinWorldSize");
            public static readonly int ProjectionScale = Shader.PropertyToID("_ProjectionScale");
            public static readonly int PointMaxSize = Shader.PropertyToID("_PointMaxSize");
            public static readonly int PointOpacityBoost = Shader.PropertyToID("_PointOpacityBoost");
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
            public static readonly int BackfaceCull = Shader.PropertyToID("_BackfaceCull");
            public static readonly int BackfaceFadeWidth = Shader.PropertyToID("_BackfaceFadeWidth");
            public static readonly int OcclusionGrid = Shader.PropertyToID("_OcclusionGrid");
            public static readonly int OcclusionGridSize = Shader.PropertyToID("_OcclusionGridSize");
            public static readonly int OcclusionCellSize = Shader.PropertyToID("_OcclusionCellSize");
            public static readonly int OcclusionFootprintScale = Shader.PropertyToID("_OcclusionFootprintScale");
            public static readonly int OcclusionTolerance = Shader.PropertyToID("_OcclusionTolerance");
            public static readonly int SplatQuadExtent = Shader.PropertyToID("_SplatQuadExtent");
            public static readonly int SplatMaxPixelRadius = Shader.PropertyToID("_SplatMaxPixelRadius");
            public static readonly int BakeQuads = Shader.PropertyToID("_BakeQuads");
            public static readonly int QuadBaseShape = Shader.PropertyToID("_QuadBaseShape");
            public static readonly int QuadBakeDeferred = Shader.PropertyToID("_QuadBakeDeferred");
            public static readonly int MatrixRigToWorld = Shader.PropertyToID("_MatrixRigToWorld");
            public static readonly int DofParams = Shader.PropertyToID("_DofParams");
            public static readonly int MatrixInvVP = Shader.PropertyToID("_MatrixInvVP");
            public static readonly int MatrixInvVPEye = Shader.PropertyToID("_MatrixInvVP_Eye");
            public static readonly int StyleRanges = Shader.PropertyToID("_StyleRanges");
            public static readonly int StyleRangeCount = Shader.PropertyToID("_StyleRangeCount");
            public static readonly int DisplayIndex = Shader.PropertyToID("_DisplayIndex");
            public static readonly int DisplayChunks = Shader.PropertyToID("_DisplayChunks");
            public static readonly int GaussianSplatRT = Shader.PropertyToID("_GaussianSplatRT");
            public static readonly int SplatSortKeys = Shader.PropertyToID("_SplatSortKeys");
            public static readonly int SplatSortDistances = Shader.PropertyToID("_SplatSortDistances");
            public static readonly int SrcBuffer = Shader.PropertyToID("_SrcBuffer");
            public static readonly int DstBuffer = Shader.PropertyToID("_DstBuffer");
            public static readonly int BufferSize = Shader.PropertyToID("_BufferSize");
            public static readonly int MatrixMV = Shader.PropertyToID("_MatrixMV");
            public static readonly int MatrixVP = Shader.PropertyToID("_MatrixVP");
            public static readonly int MatrixP = Shader.PropertyToID("_MatrixP");
            public static readonly int MatrixVPEye = Shader.PropertyToID("_MatrixVP_Eye");
            public static readonly int MatrixPEye = Shader.PropertyToID("_MatrixP_Eye");
            public static readonly int MatrixMVEye = Shader.PropertyToID("_MatrixMV_Eye");
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
            public static readonly int OdsEnabled = Shader.PropertyToID("_OdsEnabled");
            public static readonly int MatrixWorldToRig = Shader.PropertyToID("_MatrixWorldToRig");
            public static readonly int OdsParams = Shader.PropertyToID("_OdsParams");
            public static readonly int VecScreenParams = Shader.PropertyToID("_VecScreenParams");
            public static readonly int VecWorldSpaceCameraPos = Shader.PropertyToID("_VecWorldSpaceCameraPos");
            public static readonly int CameraTargetTexture = Shader.PropertyToID("_CameraTargetTexture");
            public static readonly int SelectionCenter = Shader.PropertyToID("_SelectionCenter");
            public static readonly int SelectionDelta = Shader.PropertyToID("_SelectionDelta");
            public static readonly int SelectionDeltaRot = Shader.PropertyToID("_SelectionDeltaRot");
            public static readonly int SplatCutoutsCount = Shader.PropertyToID("_SplatCutoutsCount");
            public static readonly int SplatCutouts = Shader.PropertyToID("_SplatCutouts");
            public static readonly int SelectionRect = Shader.PropertyToID("_SelectionRect");
            public static readonly int SelectionMode = Shader.PropertyToID("_SelectionMode");
            public static readonly int SplatPosMouseDown = Shader.PropertyToID("_SplatPosMouseDown");
            public static readonly int SplatOtherMouseDown = Shader.PropertyToID("_SplatOtherMouseDown");
            public static readonly int CopyDstViewData = Shader.PropertyToID("_CopyDstViewData");
            public static readonly int CopyDstSortDistances = Shader.PropertyToID("_CopyDstSortDistances");
            public static readonly int CopyDstSortKeys = Shader.PropertyToID("_CopyDstSortKeys");
            public static readonly int CopySrcStartIndex = Shader.PropertyToID("_CopySrcStartIndex");
            public static readonly int CopyDstStartIndex = Shader.PropertyToID("_CopyDstStartIndex");
            public static readonly int CopyKernelCount = Shader.PropertyToID("_CopyKernelCount");
            public static readonly int CopyWriteKeys = Shader.PropertyToID("_CopyWriteKeys");
            public static readonly int AnimOutputData = Shader.PropertyToID("_AnimOutputData");
            public static readonly int AnimDataValid = Shader.PropertyToID("_AnimDataValid");
            public static readonly int DynamicChunkBounds = Shader.PropertyToID("_DynamicChunkBounds");
            public static readonly int DynamicChunkBoundsValid = Shader.PropertyToID("_DynamicChunkBoundsValid");
            public static readonly int DeformedBase = Shader.PropertyToID("_DeformedBase");
            public static readonly int DeformedBaseValid = Shader.PropertyToID("_DeformedBaseValid");
            public static readonly int SplatVelocity = Shader.PropertyToID("_SplatVelocity");
            public static readonly int SplatVelocityValid = Shader.PropertyToID("_SplatVelocityValid");
            public static readonly int MorphedData = Shader.PropertyToID("_MorphedData");
            public static readonly int MorphDataValid = Shader.PropertyToID("_MorphDataValid");
            public static readonly int MorphWeight = Shader.PropertyToID("_MorphWeight");
            public static readonly int MorphSHData = Shader.PropertyToID("_MorphSHData");
            public static readonly int SrcSplatCount = Shader.PropertyToID("_SrcSplatCount");
            public static readonly int SortRadial = Shader.PropertyToID("_SortRadial");
            public static readonly int SortBackToFront = Shader.PropertyToID("_SortBackToFront");
            public static readonly int SortAllVisible = Shader.PropertyToID("_SortAllVisible");
            public static readonly int EyeIndex = Shader.PropertyToID("_EyeIndex");
            public static readonly int IsStereo = Shader.PropertyToID("_IsStereo");
            public static readonly int VisibleCounter = Shader.PropertyToID("_VisibleCounter");
            public static readonly int IndirectArgs = Shader.PropertyToID("_IndirectArgs");
            public static readonly int IndirectIndexCount = Shader.PropertyToID("_IndirectIndexCount");
            public static readonly int ViewProjMatrixLeft = Shader.PropertyToID("_ViewProjMatrixLeft");
            public static readonly int ViewProjMatrixRight = Shader.PropertyToID("_ViewProjMatrixRight");
            public static readonly int StereoEyeIndex = Shader.PropertyToID("_StereoEyeIndex");
        }

        // Forwarding properties for backward compatibility with Editor code
        public bool editModified => EditManager.Modified;
        public uint editSelectedSplats => EditManager.SelectedSplats;
        public uint editDeletedSplats => EditManager.DeletedSplats;
        public uint editCutSplats => EditManager.CutSplats;
        public Bounds editSelectedBounds => EditManager.SelectedBounds;

        public GaussianSplatAsset asset => splatAsset;
        public int splatCount => _splatCount;
        internal int SplatCount { get => _splatCount; set => _splatCount = value; }

        // Effective count considering morph: max(source, target) when morph is active
        internal int EffectiveSplatCount =>
            (_morphedDataBuffer != null && _morphDataValid != 0 && _morphedSplatCount > 0)
            ? _morphedSplatCount : _splatCount;

        internal GraphicsBuffer GpuPosData => _gpuPosData;
        internal GraphicsBuffer GpuOtherData => _gpuOtherData;
        internal GraphicsBuffer GpuSHData => _gpuSHData;
        internal Texture GpuColorData => _gpuColorData;
        internal GraphicsBuffer GpuChunksBuffer => _gpuChunks;
        internal bool GpuChunksValid => _gpuChunksValid;
        internal GraphicsBuffer GpuSortKeys => _gpuSortKeys;
        internal GraphicsBuffer GpuIndirectArgs => _gpuIndirectArgs;
        internal bool IndirectDrawReady => _gpuIndirectArgs != null && _gpuIndirectArgs.IsValid();
        internal GraphicsBuffer GpuIndexBuffer => _gpuIndexBuffer;
        internal GraphicsBuffer GpuView => _gpuView;
        internal GraphicsBuffer AnimOutputBuffer => _animOutputBuffer;
        internal GraphicsBuffer DeformedBase => _deformedBase;
        // Sorted keys are only valid for the camera and settings that sorted
        // them; sortNthFrame caps the refresh rate in real frames
        // (Time.frameCount), not per-camera renders. See GaussianSplatSortState.
        internal bool NeedsSort(Camera cam, in GaussianSplatSortState.Settings settings)
        {
            float threshold = GaussianSplatRenderSystem.instance.Config?.sortMoveThreshold ?? 0f;
            return _sortState.NeedsSort(cam.GetInstanceID(), settings.Key, _contentVersion,
                SortCameraPosition(cam), sortNthFrame, settings.Throttled, threshold);
        }

        internal void MarkSorted(Camera cam, in GaussianSplatSortState.Settings settings)
        {
            _sortState.MarkSorted(cam.GetInstanceID(), settings.Key, _contentVersion, SortCameraPosition(cam));
        }

        // Object-local, so moving, rotating or scaling the splat object counts
        // as camera motion for the re-sort threshold.
        private Vector3 SortCameraPosition(Camera cam) => transform.InverseTransformPoint(cam.transform.position);

        // Animated or morphing splats move every frame, so their keys go stale
        // every frame: such content never qualifies for throttled sorting.
        internal bool HasAnimatedContent =>
            _animOutputBuffer != null || _deformedBase != null || (_morphedDataBuffer != null && _morphDataValid != 0);

        // Bumped by anything that changes splat data outside of animation:
        // throttled keys are re-sorted the next time they are used.
        internal int ContentVersion => _contentVersion;
        internal void MarkContentChanged() => _contentVersion++;

        internal void SetMorphData(GraphicsBuffer morphedData, int splatCount, int valid, float weight = 0f, GraphicsBuffer morphSH = null)
        {
            _morphedDataBuffer = morphedData;
            _morphSHBuffer = morphSH;
            _morphedSplatCount = splatCount;
            _morphDataValid = valid;
            _morphWeight = weight;
            MarkContentChanged();

            int needed = EffectiveSplatCount;
            if (_gpuView != null && _gpuView.count < needed * 2)
            {
                DisposeBuffer(ref _gpuView);
                _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, needed * 2, GpuViewDataSize)
                    { name = "GaussianSplatViewData" };
            }
            if (_gpuSortDistances != null && _gpuSortDistances.count < needed)
            {
                InitSortBuffers(needed);
            }
        }

        internal void SetDeformedBase(GraphicsBuffer deformedBase, GraphicsBuffer chunkBounds)
        {
            _deformedBase = deformedBase;
            _deformedChunkBounds = chunkBounds;
        }

        internal void ClearDeformedBase()
        {
            _deformedBase = null;
            _deformedChunkBounds = null;
        }

        // Per-splat world velocity (1 float4 per splat: xyz, 0) for Velocity
        // streaks, with the fastest speed in it, which bounds how far the
        // streaks reach for culling
        internal void SetVelocity(GraphicsBuffer velocity, float maxSpeed)
        {
            _velocity = velocity;
            _velocityMaxSpeed = maxSpeed;
        }

        internal void ClearVelocity()
        {
            _velocity = null;
            _velocityMaxSpeed = 0f;
        }

        internal void SetAnimationOutput(GraphicsBuffer animBuffer, GraphicsBuffer chunkBounds)
        {
            _animOutputBuffer = animBuffer;
            _animChunkBounds = chunkBounds;
        }

        internal void ClearAnimationOutput()
        {
            _animOutputBuffer = null;
            _animChunkBounds = null;
        }

        internal void SetCutoutData(int count, GraphicsBuffer buffer)
        {
            _cutoutCount = count;
            _cutoutBuffer = buffer;
        }

        internal void ClearCutoutData()
        {
            _cutoutCount = 0;
            _cutoutBuffer = null;
        }

        internal enum KernelIndices
        {
            SetIndices,
            CalcDistances,
            CalcViewData,
            UpdateEditData,
            InitEditData,
            ClearBuffer,
            InvertSelection,
            SelectAll,
            OrBuffers,
            SelectionUpdate,
            TranslateSelection,
            RotateSelection,
            ScaleSelection,
            ExportData,
            CopySplats,
            WriteIndirectArgs,
            OcclusionClear,
            OcclusionSplat,
            OcclusionCull,
            BakeQuads,
        }

        internal static readonly string[] KernelNames =
        {
            "CSSetIndices",
            "CSCalcDistances",
            "CSCalcViewData",
            "CSUpdateEditData",
            "CSInitEditData",
            "CSClearBuffer",
            "CSInvertSelection",
            "CSSelectAll",
            "CSOrBuffers",
            "CSSelectionUpdate",
            "CSTranslateSelection",
            "CSRotateSelection",
            "CSScaleSelection",
            "CSExportData",
            "CSCopySplats",
            "CSWriteIndirectArgs",
            "CSOcclusionClear",
            "CSOcclusionSplat",
            "CSOcclusionCull",
            "CSBakeQuads",
        };

        const string CopyViewDataAndDistancesKernelName = "CSCopyViewDataAndDistances";

        public bool HasValidAsset =>
            splatAsset != null &&
            splatAsset.splatCount > 0 &&
            splatAsset.formatVersion == GaussianSplatAsset.CurrentVersion &&
            splatAsset.posData != null &&
            splatAsset.otherData != null &&
            (splatAsset.shData != null || splatAsset.shDegree == 0) &&
            splatAsset.colorData != null;
        // Must cover every resource the draw path binds: between
        // InitResourcesForAssets (pos/view created) and CreateResourcesForAsset
        // (sort buffers created) a transition-frame render would otherwise draw
        // with unbound _OrderBuffer/_SplatViewData and get skipped by Vulkan.
        public bool HasValidRenderSetup => _gpuPosData != null && _gpuOtherData != null && _gpuChunks != null &&
                                           _gpuView != null && _gpuSortKeys != null && _gpuIndexBuffer != null;

        internal const int GpuViewDataSize = 40;

        private bool ResourcesAreSetUp => csSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void InitResourcesForAssets(long posDataMaxSize, long otherDataMaxSize, long shDataMaxSize, long chunkDataMaxSize, long splatCountMaxSize)
        {
            _preallocatedSplatCount = (int)splatCountMaxSize;
            // Dispose any buffers created earlier (e.g. by OnEnable for the inspector asset)
            DisposeBuffer(ref _gpuPosData);
            DisposeBuffer(ref _gpuOtherData);
            DisposeBuffer(ref _gpuSHData);
            DisposeBuffer(ref _gpuChunks);
            DisposeBuffer(ref _gpuView);
            DisposeBuffer(ref _gpuIndexBuffer);

            _gpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(posDataMaxSize / 4), 4) { name = "GaussianPosData" };
            _gpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(otherDataMaxSize / 4), 4) { name = "GaussianOtherData" };
            _gpuSHData = CreateSHBuffer(shDataMaxSize);
            if (chunkDataMaxSize != 0)
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int)(chunkDataMaxSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
            else
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
            _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)splatCountMaxSize * 2, GpuViewDataSize);
            _gpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            _gpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });
        }
        public void InitResourcesForAsset()
        {
            _preallocatedSplatCount = 0;
            _gpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            _gpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            _gpuSHData = CreateSHBuffer(asset.SHDataSize);
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int)(asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
            }
            else
            {
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
            }
            _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatAsset.splatCount * 2, GpuViewDataSize);
            _gpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            _gpuIndexBuffer.SetData(new ushort[]
            {
                0, 1, 2, 1, 3, 2,
                4, 6, 5, 5, 6, 7,
                0, 2, 4, 4, 2, 6,
                1, 5, 3, 5, 7, 3,
                0, 4, 1, 4, 5, 1,
                2, 3, 6, 3, 7, 6
            });
        }
        // Existing buffers may be intentionally oversized (GaussianPlayer
        // preallocates for the largest frame) — only recreate when too small.
        private bool ResourcesFitAsset()
        {
            if (_gpuPosData == null || _gpuOtherData == null || _gpuSHData == null ||
                _gpuChunks == null || _gpuView == null || _gpuIndexBuffer == null)
                return false;
            if ((long)_gpuPosData.count * 4 < asset.posData.dataSize ||
                (long)_gpuOtherData.count * 4 < asset.otherData.dataSize ||
                (long)_gpuSHData.count * 4 < asset.SHDataSize)
                return false;
            if (_gpuView.count < asset.splatCount * 2)
                return false;
            bool assetChunked = asset.chunkData != null && asset.chunkData.dataSize != 0;
            if (assetChunked &&
                (long)_gpuChunks.count * UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>() < asset.chunkData.dataSize)
                return false;
            return true;
        }

        private void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;
            if (!ResourcesFitAsset())
            {
                DisposeBuffer(ref _gpuPosData);
                DisposeBuffer(ref _gpuOtherData);
                DisposeBuffer(ref _gpuSHData);
                DisposeBuffer(ref _gpuChunks);
                DisposeBuffer(ref _gpuView);
                DisposeBuffer(ref _gpuIndexBuffer);
                InitResourcesForAsset();
            }

            _splatCount = asset.splatCount;
            _gpuPosData.SetData(asset.posData.GetData<uint>());
            _gpuOtherData.SetData(asset.otherData.GetData<uint>());
            if (asset.shData != null)
                _gpuSHData.SetData(asset.shData.GetData<uint>());

            UploadColorData();
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                _gpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                _gpuChunksValid = true;
            }
            else
            {
                _gpuChunksValid = false;
            }

            InitSortBuffers(splatCount);
        }

        // A preallocated renderer (GaussianPlayer) swaps assets every few
        // frames. It keeps one CPU-readable color texture tall enough for the
        // largest frame and copies each frame into it, instead of creating and
        // destroying a texture per frame. The texture is 2048 wide with the
        // swizzle fixed per row, so a shorter frame simply leaves the bottom
        // rows unused.
        private void UploadColorData()
        {
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var src = asset.colorData.GetData<byte>();

            // `is` skips Unity's destroyed-object check, hence the explicit != null.
            if (_gpuColorData is Texture2D current && current != null && current.isReadable && current.width == texWidth &&
                current.height >= texHeight && current.graphicsFormat == texFormat)
            {
                NativeArray<byte>.Copy(src, current.GetPixelData<byte>(0), src.Length);
                current.Apply(false, false);
                return;
            }

            int allocHeight = _preallocatedSplatCount > 0
                ? Mathf.Max(texHeight, GaussianSplatAsset.CalcTextureSize(_preallocatedSplatCount).Item2)
                : texHeight;
            var tex = new Texture2D(texWidth, allocHeight, texFormat,
                TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate)
                { name = "GaussianColorData" };
            if (allocHeight == texHeight)
                tex.SetPixelData(src, 0);
            else
                NativeArray<byte>.Copy(src, tex.GetPixelData<byte>(0), src.Length);
            // Only a preallocated texture is reused, so only it keeps its CPU copy.
            tex.Apply(false, _preallocatedSplatCount == 0);
            if (_gpuColorData != null)
                DestroyImmediate(_gpuColorData);
            _gpuColorData = tex;
        }

        // Sort buffers only grow: an asset swap within capacity (GaussianPlayer
        // frames, morph targets) reuses them and just resets their contents.
        internal void InitSortBuffers(int count)
        {
            EnsureSorterAndRegister();
            // Old keys hold no valid order for the new content.
            _sortState.Invalidate();
            MarkContentChanged();

            bool fits = _gpuSortDistances != null && _gpuSortDistances.count >= count &&
                        _gpuSortKeys != null && _gpuVisibleCounter != null && _gpuIndirectArgs != null &&
                        (_sorter == null || !_sorter.Valid || _sorterArgs.resources.altKeys != null);
            if (!fits)
            {
                DisposeBuffer(ref _gpuSortDistances);
                DisposeBuffer(ref _gpuSortKeys);
                DisposeBuffer(ref _gpuVisibleCounter);
                DisposeBuffer(ref _gpuIndirectArgs);
                _sorterArgs.resources.Dispose();

                _gpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
                _gpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };
                _gpuVisibleCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "GaussianSplatVisibleCounter" };
                _gpuIndirectArgs = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 5, 4) { name = "GaussianSplatIndirectArgs" };
                if (_sorter != null && _sorter.Valid)
                    _sorterArgs.resources = GpuSortResources.Load((uint)count);
            }
            // Until the first sort runs (or if no sorter is available), the
            // indirect draw covers every splat, matching the old direct draw.
            _gpuIndirectArgs.SetData(new uint[] { 6, (uint)count, 0, 0, 0 });

            // init keys buffer to splat indices
            if (TryFindSupportedKernel(KernelIndices.SetIndices, out int kernelIndex))
            {
                csSplatUtilities.SetBuffer(kernelIndex, Props.SplatSortKeys, _gpuSortKeys);
                csSplatUtilities.SetInt(Props.SplatCount, _gpuSortDistances.count);
                csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
                csSplatUtilities.Dispatch(kernelIndex, (_gpuSortDistances.count + (int)gsX - 1)/(int)gsX, 1, 1);
            }
            else
            {
                uint[] keys = new uint[count];
                for (uint i = 0; i < count; ++i)
                    keys[i] = i;
                _gpuSortKeys.SetData(keys);
            }

            _sorterArgs.inputKeys = _gpuSortDistances;
            _sorterArgs.inputValues = _gpuSortKeys;
            _sorterArgs.count = (uint)count;
            // Distance keys: low 16 bits are sub-bucket noise, skip them (2x faster).
            _sorterArgs.sortStartBit = 16;
        }

        internal bool TryFindSupportedKernel(string kernelName, out int kernelIndex)
        {
            kernelIndex = -1;
            if (csSplatUtilities == null || string.IsNullOrEmpty(kernelName))
                return false;
            if (_kernelIndexCache.TryGetValue(kernelName, out kernelIndex))
                return csSplatUtilities.IsSupported(kernelIndex);
            try
            {
                kernelIndex = csSplatUtilities.FindKernel(kernelName);
            }
            catch (System.Exception)
            {
                return false;
            }
            if (kernelIndex < 0)
                return false;
            if (!csSplatUtilities.IsSupported(kernelIndex))
                return false;
            _kernelIndexCache[kernelName] = kernelIndex;
            return true;
        }

        internal bool TryFindSupportedKernel(KernelIndices kernel, out int kernelIndex)
        {
            kernelIndex = -1;
            int idx = (int)kernel;
            if (idx < 0 || idx >= KernelNames.Length)
                return false;
            return TryFindSupportedKernel(KernelNames[idx], out kernelIndex);
        }

        public void EnsureSorterAndRegister()
        {
            if (_sorter == null && ResourcesAreSetUp)
                _sorter = GpuSorters.Create(csSplatSort, csPortableSort);

            if (!_registered && ResourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                _registered = true;
            }
        }

        private void OnEnable()
        {
            _sortState.Invalidate();

            if (!ResourcesAreSetUp)
                return;

            EnsureSorterAndRegister();
            CreateResourcesForAsset();
        }

        internal bool SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel, out int kernelIndex)
        {
            if (!TryFindSupportedKernel(kernel, out kernelIndex))
                return false;
            SetAssetDataOnCS(cmb, kernelIndex);
            return true;
        }

        internal void SetAssetDataOnCS(CommandBuffer cmb, int kernelIndex)
        {
            SetAssetDataOnCS(cmb, csSplatUtilities, kernelIndex);
        }

        // Binds the splats as the view data pass reads them (asset data plus
        // morph, mesh driver and animation output) to another shader's kernel
        // that includes GaussianSplatting.hlsl, e.g. GaussianParticles
        internal void SetAssetDataOnCS(CommandBuffer cmb, ComputeShader cs, int kernelIndex)
        {
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, _gpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, _gpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, _gpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, _gpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, _gpuColorData);
            var editSelected = EditManager.GpuEditSelected;
            var editDeleted = EditManager.GpuEditDeleted;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, editSelected ?? _gpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, editDeleted ?? _gpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, _gpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, _gpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, editSelected != null && editDeleted != null ? 1 : 0);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)splatAsset.FormatFlags);
            cmb.SetComputeIntParam(cs, Props.SplatCount, EffectiveSplatCount);
            cmb.SetComputeIntParam(cs, Props.SrcSplatCount, _splatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, _gpuChunksValid ? _gpuChunks.count : 0);

            bool hasCutouts = _cutoutBuffer != null && _cutoutCount > 0;
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, hasCutouts ? _cutoutCount : 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, hasCutouts ? _cutoutBuffer : _gpuView);

            // Animation data binding
            bool hasAnim = _animOutputBuffer != null;
            cmb.SetComputeIntParam(cs, Props.AnimDataValid, hasAnim ? 1 : 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.AnimOutputData, hasAnim ? _animOutputBuffer : _gpuView);

            // Mesh driver base pose. The animation reads it too, so its
            // bounds already cover it.
            bool hasDeformed = _deformedBase != null;
            cmb.SetComputeIntParam(cs, Props.DeformedBaseValid, hasDeformed ? 1 : 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.DeformedBase, hasDeformed ? _deformedBase : _gpuView);
            GraphicsBuffer chunkBounds = hasAnim ? _animChunkBounds : hasDeformed ? _deformedChunkBounds : null;
            cmb.SetComputeIntParam(cs, Props.DynamicChunkBoundsValid, chunkBounds != null ? 1 : 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.DynamicChunkBounds, chunkBounds ?? _gpuView);
            SetVelocityOnCS(cmb, cs, kernelIndex);

            // Morph data binding
            bool hasMorph = _morphedDataBuffer != null && _morphDataValid != 0;
            bool hasMorphSH = hasMorph && _morphSHBuffer != null;
            cmb.SetComputeIntParam(cs, Props.MorphDataValid, hasMorphSH ? 2 : (hasMorph ? 1 : 0));
            cmb.SetComputeFloatParam(cs, Props.MorphWeight, hasMorph ? _morphWeight : 0f);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.MorphedData, hasMorph ? _morphedDataBuffer : _gpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.MorphSHData, hasMorphSH ? _morphSHBuffer : _gpuView);
        }

        private void SetVelocityOnCS(CommandBuffer cmb, ComputeShader cs, int kernelIndex)
        {
            bool hasVelocity = _velocity != null;
            cmb.SetComputeIntParam(cs, Props.SplatVelocityValid, hasVelocity ? 1 : 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatVelocity, hasVelocity ? _velocity : _gpuView);
        }

        internal void SetAssetDataOnMaterial(MaterialPropertyBlock mat)
        {
            mat.SetBuffer(Props.SplatPos, _gpuPosData);
            mat.SetBuffer(Props.SplatOther, _gpuOtherData);
            mat.SetBuffer(Props.SplatSH, _gpuSHData);
            mat.SetTexture(Props.SplatColor, _gpuColorData);
            var editSelected = EditManager.GpuEditSelected;
            var editDeleted = EditManager.GpuEditDeleted;
            mat.SetBuffer(Props.SplatSelectedBits, editSelected ?? _gpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, editDeleted ?? _gpuPosData);
            mat.SetInt(Props.SplatBitsValid, editSelected != null && editDeleted != null ? 1 : 0);
            mat.SetInteger(Props.SplatFormat, (int)splatAsset.FormatFlags);
            mat.SetInteger(Props.SplatCount, _splatCount);
            mat.SetInteger(Props.SplatChunkCount, _gpuChunksValid ? _gpuChunks.count : 0);
        }

        // An asset without SH has no SH data, but the shaders still bind a buffer
        internal static GraphicsBuffer CreateSHBuffer(long size)
        {
            return new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)Math.Max(1, size / 4), 4) { name = "GaussianSHData" };
        }

        private static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            // Deferred: with a threaded graphics device this frame's recorded
            // draws may still reference the buffer on the render thread.
            GaussianSplatRenderSystem.DeferDispose(buf);
            buf = null;
        }

        private void DisposeResourcesForAsset()
        {
            DestroyImmediate(_gpuColorData);
            _gpuColorData = null;

            DisposeBuffer(ref _gpuPosData);
            DisposeBuffer(ref _gpuOtherData);
            DisposeBuffer(ref _gpuSHData);
            DisposeBuffer(ref _gpuChunks);

            DisposeBuffer(ref _gpuView);
            DisposeBuffer(ref _gpuIndexBuffer);
            DisposeBuffer(ref _gpuSortDistances);
            DisposeBuffer(ref _gpuSortKeys);
            DisposeBuffer(ref _gpuVisibleCounter);
            DisposeBuffer(ref _gpuIndirectArgs);
            DisposeBuffer(ref _gpuOcclusionGrid);
            DisposeBuffer(ref _gpuStyleRange);
            _styleSet.Dispose();

            _sorterArgs.resources.Dispose();

            _splatCount = 0;
            _gpuChunksValid = false;
        }

        private void OnDisable()
        {
            // Unregister before disposing so a same-frame gather never sees a
            // registered renderer whose buffers are already released.
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            _registered = false;
            _editManager?.Dispose();
            _editManager = null;
            DisposeResourcesForAsset();
            _kernelIndexCache.Clear();
        }

        // Single-pass-instanced stereo capture: view data is laid out as two
        // entries per splat. Sorting and view-data code must agree on this.
        internal static bool IsStereoCapture(Camera cam)
        {
            return XRSettings.enabled && cam.stereoEnabled &&
                   (XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced ||
                    XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview) &&
                   !Application.isEditor;
        }

        internal bool CalcViewData(CommandBuffer cmb, Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;

            var tr = transform;

            // SRP features may render this camera with pipeline-level matrices
            // that never touch the Camera object (cubemap faces during 360
            // capture); an active override is then the source of truth.
            var matrixOverride = GaussianSplatRenderSystem.instance.MatrixOverride;
            Matrix4x4 matView = matrixOverride?.View ?? cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            Vector4 screenPar = new Vector4(eyeW != 0 ? eyeW : screenW, eyeH != 0 ? eyeH : screenH, 0, 0);
            if (matrixOverride is { ScreenSize: { x: > 0, y: > 0 } overrideSize })
                screenPar = new Vector4(overrideSize.x, overrideSize.y, 0, 0);
            Vector4 camPos = cam.transform.position;

            if (!SetAssetDataOnCS(cmb, KernelIndices.CalcViewData, out int kernelIndex))
                return false;

            // Use renderIntoTexture=true: we always render to an intermediate RT,
            // so the VP must include Metal's Y-flip for correct orientation.
            // Do NOT use SetViewProjectionMatrices — it does NOT update UNITY_MATRIX_VP
            // for compute dispatches on Metal/visionOS (confirmed via diagnostics).
            // Also avoid SetViewProjectionMatrices to prevent corrupting Unity's
            // stereo rendering state (which caused mesh left/right eye swap).
            Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(matrixOverride?.Proj ?? cam.projectionMatrix, true);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, matView * matO2W);
            Matrix4x4 matVP = gpuProj * matView;
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixVP, matVP);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixP, gpuProj);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixWorldToObject, matW2O);

            // ODS bypasses _MatrixVP / _MatrixP entirely: the eye position rides
            // a circle that rotates with the view azimuth, so no single
            // view-projection matrix exists. Bind the rig transform and the
            // circle parameters instead.
            var odsConfig = GaussianSplatRenderSystem.instance.Config;
            bool useOds = odsConfig != null &&
                          odsConfig.projectionMode == GaussianSplatProjectionMode.OmniDirectionalStereo;
            cmb.SetComputeIntParam(csSplatUtilities, Props.OdsEnabled, useOds ? 1 : 0);
            if (useOds)
            {
                // Built from position/rotation only: a scaled camera transform
                // would otherwise leak into the rig-local splat positions.
                Transform camTr = cam.transform;
                Matrix4x4 rigToWorld = Matrix4x4.TRS(camTr.position, camTr.rotation, Vector3.one);
                cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixWorldToRig, rigToWorld.inverse);
                cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixRigToWorld, rigToWorld);

                // smoothstep needs edge0 < edge1, and gpuProj.m11's sign is the
                // render-target Y flip that a projection matrix would normally
                // apply — without one, ODS has to carry that sign itself or the
                // equirect comes out vertically mirrored.
                float mergeEnd = Mathf.Max(odsConfig.odsPoleMergeEnd, 0.01f);
                float mergeStart = Mathf.Min(odsConfig.odsPoleMergeStart, mergeEnd - 0.01f);
                cmb.SetComputeVectorParam(csSplatUtilities, Props.OdsParams, new Vector4(
                    odsConfig.odsEyeOffset,
                    mergeStart * Mathf.Deg2Rad,
                    mergeEnd * Mathf.Deg2Rad,
                    gpuProj.m11 < 0f ? -1f : 1f));
            }

            // Stereo rendering: pass per-eye view-projection matrices
            bool isStereo = IsStereoCapture(cam);

            cmb.SetComputeVectorParam(csSplatUtilities, Props.VecScreenParams, screenPar);
            // Also set as global so the render shader can use the same screen params
            cmb.SetGlobalVector(Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(csSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(csSplatUtilities, Props.SplatScale, splatScale);
            cmb.SetComputeFloatParam(csSplatUtilities, Props.SplatOpacityScale, opacityScale);
            // The view-data pass reads only the SH bands it shades: a lower
            // order trades view-dependent shading for bandwidth, and order 0
            // skips every SH read.
            cmb.SetComputeIntParam(csSplatUtilities, Props.SHOrder, EffectiveSHOrder);
            cmb.SetComputeIntParam(csSplatUtilities, Props.SHOnly, shOnly ? 1 : 0);
            cmb.SetComputeIntParam(csSplatUtilities, Props.BackfaceCull, backfaceCulling ? 1 : 0);
            cmb.SetComputeFloatParam(csSplatUtilities, Props.BackfaceFadeWidth, Mathf.Sin(backfaceFadeAngle * Mathf.Deg2Rad));
            // Point and style sizes turn a world size into pixels: cot(fovY/2), or 2/pi
            // under ODS, whose equirect spreads 2 pi of azimuth over a 2:1 target
            float projectionScale = useOds ? 2f / Mathf.PI : (matrixOverride?.Proj ?? cam.projectionMatrix).m11;
            GaussianDepthOfField dof = GaussianDepthOfField.ActiveOn(cam);
            cmb.SetComputeVectorParam(csSplatUtilities, Props.DofParams, dof != null ? dof.ShaderParams : Vector4.zero);
            // The occlusion grid takes blurred splats for solid surfaces
            bool occlusion = occlusionCulling && dof == null;
            SetStyleOnCS(cmb, kernelIndex, projectionScale, occlusion);

            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            int dispatchCount = EffectiveSplatCount;
            int groupCount = (dispatchCount + (int)gsX - 1) / (int)gsX;

            // Per-eye view-projections, for a deferred style shape pass
            Matrix4x4[] eyeVP = null;
            if (isStereo)
            {
                // Single-dispatch stereo: pass both eyes' matrices and dispatch once.
                // The CSCalcViewDataStereo kernel computes view-independent data (3D
                // covariance, SH) once, then per-eye data (clip pos, 2D cov) in a loop.
                cmb.SetComputeIntParam(csSplatUtilities, Props.IsStereo, 1);

                if (TryFindSupportedKernel("CSCalcViewDataStereo", out int stereoKernel))
                {
                    Matrix4x4 leftView = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
                    Matrix4x4 rightView = cam.GetStereoViewMatrix(Camera.StereoscopicEye.Right);
                    Matrix4x4 leftGpu = GL.GetGPUProjectionMatrix(
                        cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), true);
                    Matrix4x4 rightGpu = GL.GetGPUProjectionMatrix(
                        cam.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), true);

                    eyeVP = new[] { leftGpu * leftView, rightGpu * rightView };
                    cmb.SetComputeMatrixArrayParam(csSplatUtilities, Props.MatrixVPEye, eyeVP);
                    cmb.SetComputeMatrixArrayParam(csSplatUtilities, Props.MatrixPEye,
                        new[] { leftGpu, rightGpu });
                    cmb.SetComputeMatrixArrayParam(csSplatUtilities, Props.MatrixMVEye,
                        new[] { leftView * matO2W, rightView * matO2W });

                    SetAssetDataOnCS(cmb, stereoKernel);
                    SetStyleOnCS(cmb, stereoKernel, projectionScale, occlusion);
                    csSplatUtilities.GetKernelThreadGroupSizes(stereoKernel, out uint gsX2, out _, out _);
                    int stereoGroupCount = (dispatchCount + (int)gsX2 - 1) / (int)gsX2;
                    cmb.DispatchCompute(csSplatUtilities, stereoKernel, stereoGroupCount, 1, 1);
                }
                else
                {
                    // Fallback: per-eye dispatch if stereo kernel not available
                    for (int eye = 0; eye < 2; eye++)
                    {
                        var stereoEye = eye == 0
                            ? Camera.StereoscopicEye.Left
                            : Camera.StereoscopicEye.Right;
                        Matrix4x4 stereoView = cam.GetStereoViewMatrix(stereoEye);
                        Matrix4x4 stereoGpuProj = GL.GetGPUProjectionMatrix(
                            cam.GetStereoProjectionMatrix(stereoEye), true);

                        eyeVP ??= new Matrix4x4[2];
                        eyeVP[eye] = stereoGpuProj * stereoView;
                        cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixVP, eyeVP[eye]);
                        cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixP, stereoGpuProj);
                        cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, stereoView * matO2W);
                        cmb.SetComputeIntParam(csSplatUtilities, Props.StereoEyeIndex, eye);

                        cmb.DispatchCompute(csSplatUtilities, kernelIndex, groupCount, 1, 1);
                    }
                }
            }
            else
            {
                cmb.SetComputeIntParam(csSplatUtilities, Props.IsStereo, 0);
                cmb.DispatchCompute(csSplatUtilities, kernelIndex, groupCount, 1, 1);
            }

            if (occlusion)
                CullOccludedSplats(cmb, screenPar, isStereo);
            if (QuadsBaked && occlusion)
                BakeDeferredQuads(cmb, matVP, eyeVP);
            return true;
        }

        // Quads held back while occlusion culling reads the Gaussian footprints
        // they replace (see _QuadBakeDeferred). Stereo when eyeVP is set.
        private void BakeDeferredQuads(CommandBuffer cmb, Matrix4x4 vp, Matrix4x4[] eyeVP)
        {
            if (!TryFindSupportedKernel(KernelIndices.BakeQuads, out int kernel))
                return;

            ComputeShader cs = csSplatUtilities;
            bool stereo = eyeVP != null;
            cmb.SetComputeIntParam(cs, Props.IsStereo, stereo ? 1 : 0);
            if (stereo)
            {
                cmb.SetComputeMatrixArrayParam(cs, Props.MatrixVPEye, eyeVP);
                cmb.SetComputeMatrixArrayParam(cs, Props.MatrixInvVPEye, new[] { eyeVP[0].inverse, eyeVP[1].inverse });
            }
            else
            {
                cmb.SetComputeMatrixParam(cs, Props.MatrixVP, vp);
                cmb.SetComputeMatrixParam(cs, Props.MatrixInvVP, vp.inverse);
            }
            cmb.SetComputeBufferParam(cs, kernel, Props.SplatViewData, _gpuView);
            SetVelocityOnCS(cmb, cs, kernel);
            cs.GetKernelThreadGroupSizes(kernel, out uint group, out _, out _);
            cmb.DispatchCompute(cs, kernel, (EffectiveSplatCount + (int)group - 1) / (int)group, 1, 1);
        }

        // Runs on the view data CalcViewData just wrote, with its screen
        // parameters and stereo flag still bound
        private void CullOccludedSplats(CommandBuffer cmb, Vector4 screenPar, bool isStereo)
        {
            if (!TryFindSupportedKernel(KernelIndices.OcclusionClear, out int clearKernel) ||
                !TryFindSupportedKernel(KernelIndices.OcclusionSplat, out int splatKernel) ||
                !TryFindSupportedKernel(KernelIndices.OcclusionCull, out int cullKernel))
                return;

            int gridW = Mathf.CeilToInt(screenPar.x / OcclusionCellSize);
            int gridH = Mathf.CeilToInt(screenPar.y / OcclusionCellSize);
            int cellCount = gridW * gridH * (isStereo ? 2 : 1);
            if (cellCount <= 0)
                return;
            if (_gpuOcclusionGrid == null || _gpuOcclusionGrid.count < cellCount)
            {
                DisposeBuffer(ref _gpuOcclusionGrid);
                _gpuOcclusionGrid = new GraphicsBuffer(GraphicsBuffer.Target.Raw, cellCount, 4) { name = "GaussianOcclusionGrid" };
            }

            ComputeShader cs = csSplatUtilities;
            cmb.SetComputeIntParams(cs, Props.OcclusionGridSize, gridW, gridH);
            cmb.SetComputeFloatParam(cs, Props.OcclusionCellSize, OcclusionCellSize);
            cmb.SetComputeFloatParam(cs, Props.OcclusionFootprintScale, occlusionFootprintScale);
            cmb.SetComputeFloatParam(cs, Props.OcclusionTolerance, occlusionTolerance);

            cmb.SetComputeBufferParam(cs, clearKernel, Props.OcclusionGrid, _gpuOcclusionGrid);
            cs.GetKernelThreadGroupSizes(clearKernel, out uint clearGroup, out _, out _);
            cmb.DispatchCompute(cs, clearKernel, (cellCount + (int)clearGroup - 1) / (int)clearGroup, 1, 1);

            DispatchPerSplat(splatKernel);
            DispatchPerSplat(cullKernel);

            void DispatchPerSplat(int kernel)
            {
                cmb.SetComputeBufferParam(cs, kernel, Props.OcclusionGrid, _gpuOcclusionGrid);
                cmb.SetComputeBufferParam(cs, kernel, Props.SplatViewData, _gpuView);
                cs.GetKernelThreadGroupSizes(kernel, out uint group, out _, out _);
                cmb.DispatchCompute(cs, kernel, (EffectiveSplatCount + (int)group - 1) / (int)group, 1, 1);
            }
        }

        // Style color and baked quads are computed with the view data; the
        // draw-side parameters live in GaussianSplatRenderSystem
        private void SetStyleOnCS(CommandBuffer cmb, int kernelIndex, float projectionScale, bool occlusion)
        {
            ComputeShader cs = csSplatUtilities;
            SetStyleColorOnCS(cmb, cs, kernelIndex);
            // Read whether or not a style is set: the culling margins, and the
            // additivity occlusion culling discounts
            _styleSet.SetShapeOnCS(cmb, cs, StyleActive, _velocity != null ? _velocityMaxSpeed : 0f, transform.lossyScale);

            bool baked = QuadsBaked;
            cmb.SetComputeIntParam(cs, Props.BakeQuads, baked ? 1 : 0);
            cmb.SetComputeIntParam(cs, Props.QuadBakeDeferred, baked && occlusion ? 1 : 0);
            if (baked)
            {
                // The baked quads reproduce the shaping the render mode's own
                // shader would apply
                var config = GaussianSplatRenderSystem.instance.Config;
                cmb.SetComputeIntParam(cs, Props.QuadBaseShape, PointCloudMode ? 1 : 0);
                cmb.SetComputeFloatParam(cs, Props.SplatQuadExtent, config.splatQuadSigma / Mathf.Sqrt(2f));
                cmb.SetComputeFloatParam(cs, Props.SplatMaxPixelRadius, config.maxSplatPixelRadius);
                cmb.SetComputeFloatParam(cs, Props.PointSizeScale, config.pointCloudSizeScale);
                cmb.SetComputeFloatParam(cs, Props.PointMinSize, config.pointCloudMinDisplaySize);
                cmb.SetComputeFloatParam(cs, Props.PointMaxSize, config.pointCloudMaxDisplaySize);
                cmb.SetComputeFloatParam(cs, Props.PointMinWorldSize, config.pointCloudMinWorldSize);
                cmb.SetComputeFloatParam(cs, Props.ProjectionScale, projectionScale);
                cmb.SetComputeFloatParam(cs, Props.PointOpacityBoost, config.pointCloudOpacityBoost);
            }
        }

        // The style color parameters (GaussianStyle.hlsl): for the view data,
        // and for another compute shader that colors splats the way this
        // renderer draws them, e.g. GaussianParticleEmitter. Styles only apply
        // in the render modes that draw them (StyleActive).
        internal void SetStyleColorOnCS(CommandBuffer cmb, ComputeShader cs, int kernelIndex) =>
            _styleSet.SetColorOnCS(cmb, cs, kernelIndex, StyleActive);

        internal bool SupportsGlobalSortPath()
        {
            return csSplatUtilities != null &&
                   TryFindSupportedKernel(KernelIndices.CalcViewData, out _) &&
                   TryFindSupportedKernel(CopyViewDataAndDistancesKernelName, out _);
        }

        internal bool CopyViewDataAndDistances(CommandBuffer cmb, Camera cam, Matrix4x4 matrix,
            GraphicsBuffer dstViewData, GraphicsBuffer dstSortDistances, GraphicsBuffer dstSortKeys,
            GraphicsBuffer visibleCounter, int srcStartIndex, int dstStartIndex, int copyCount, bool writeSortKeys,
            in GaussianSplatSortState.Settings settings)
        {
            if (copyCount <= 0)
                return true;
            if (!TryFindSupportedKernel(CopyViewDataAndDistancesKernelName, out int kernelIndex))
                return false;
            SetAssetDataOnCS(cmb, kernelIndex);

            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, SortViewMatrix(cam) * matrix);
            SetSortSettingsOnCS(cmb, settings);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.CopyDstViewData, dstViewData);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.CopyDstSortDistances, dstSortDistances);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.CopyDstSortKeys, dstSortKeys);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.VisibleCounter, visibleCounter);
            cmb.SetComputeIntParam(csSplatUtilities, Props.CopySrcStartIndex, srcStartIndex);
            cmb.SetComputeIntParam(csSplatUtilities, Props.CopyDstStartIndex, dstStartIndex);
            cmb.SetComputeIntParam(csSplatUtilities, Props.CopyKernelCount, copyCount);
            cmb.SetComputeIntParam(csSplatUtilities, Props.CopyWriteKeys, writeSortKeys ? 1 : 0);

            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            cmb.DispatchCompute(csSplatUtilities, kernelIndex, (copyCount + (int)gsX - 1)/(int)gsX, 1, 1);
            return true;
        }

        // World to sort-view space: camera space with z pointing forward. The
        // whole z row flips, translation included; flipping only the rotation
        // shifts z by a camera-dependent constant, which view-depth order
        // ignores but radial distance does not.
        private static Matrix4x4 SortViewMatrix(Camera cam)
        {
            Matrix4x4 view = GaussianSplatRenderSystem.instance.MatrixOverride?.View ?? cam.worldToCameraMatrix;
            view.SetRow(2, -view.GetRow(2));
            return view;
        }

        private void SetSortSettingsOnCS(CommandBuffer cmd, in GaussianSplatSortState.Settings settings)
        {
            cmd.SetComputeIntParam(csSplatUtilities, Props.SortRadial, settings.Radial ? 1 : 0);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SortBackToFront, settings.BackToFront ? 1 : 0);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SortAllVisible, settings.Throttled ? 1 : 0);
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix, in GaussianSplatSortState.Settings settings)
        {
            if (cam.cameraType == CameraType.Preview)
                return;
            if (!TryFindSupportedKernel(KernelIndices.CalcDistances, out int kernelIndex))
                return;

            cmd.BeginSample(ProfSort);

            cmd.SetBufferData(_gpuVisibleCounter, ZeroCounterData);

            // Reset keys to identity before computing distances: equal keys
            // then always resolve in splat-index order (the sorters are
            // stable), instead of in whatever order the previous sort left.
            if (TryFindSupportedKernel(KernelIndices.SetIndices, out int setIndicesKernel))
            {
                cmd.SetComputeBufferParam(csSplatUtilities, setIndicesKernel, Props.SplatSortKeys, _gpuSortKeys);
                cmd.SetComputeIntParam(csSplatUtilities, Props.SplatCount, EffectiveSplatCount);
                csSplatUtilities.GetKernelThreadGroupSizes(setIndicesKernel, out uint giX, out _, out _);
                cmd.DispatchCompute(csSplatUtilities, setIndicesKernel, (EffectiveSplatCount + (int)giX - 1) / (int)giX, 1, 1);
            }

            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatSortDistances, _gpuSortDistances);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatSortKeys, _gpuSortKeys);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatViewData, _gpuView);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.VisibleCounter, _gpuVisibleCounter);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatChunks, _gpuChunks);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatPos, _gpuPosData);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatFormat, (int)splatAsset.FormatFlags);
            cmd.SetComputeIntParam(csSplatUtilities, Props.IsStereo, IsStereoCapture(cam) ? 1 : 0);
            cmd.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, SortViewMatrix(cam) * matrix);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatCount, EffectiveSplatCount);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SrcSplatCount, _splatCount);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatChunkCount, _gpuChunksValid ? _gpuChunks.count : 0);
            SetSortSettingsOnCS(cmd, settings);

            bool hasMorph = _morphedDataBuffer != null && _morphDataValid != 0;
            cmd.SetComputeIntParam(csSplatUtilities, Props.MorphDataValid, hasMorph ? 1 : 0);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.MorphedData, hasMorph ? _morphedDataBuffer : _gpuSortDistances);

            // Sort on the animated position, the one that is actually drawn.
            bool hasAnim = _animOutputBuffer != null;
            cmd.SetComputeIntParam(csSplatUtilities, Props.AnimDataValid, hasAnim ? 1 : 0);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.AnimOutputData, hasAnim ? _animOutputBuffer : _gpuSortDistances);
            bool hasDeformed = _deformedBase != null;
            cmd.SetComputeIntParam(csSplatUtilities, Props.DeformedBaseValid, hasDeformed ? 1 : 0);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.DeformedBase, hasDeformed ? _deformedBase : _gpuSortDistances);

            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            cmd.DispatchCompute(csSplatUtilities, kernelIndex, (EffectiveSplatCount + (int)gsX - 1)/(int)gsX, 1, 1);

            bool sorted = _sorter != null && _sorter.Valid;
            if (sorted)
            {
                _sorterArgs.count = (uint)EffectiveSplatCount;
                _sorter.Dispatch(cmd, _sorterArgs);
            }

            // Compaction is only valid once sentinel keys are sorted to the
            // tail; without a sorter the args keep their full-count init.
            if (sorted && TryFindSupportedKernel(KernelIndices.WriteIndirectArgs, out int argsKernel))
            {
                cmd.SetComputeBufferParam(csSplatUtilities, argsKernel, Props.VisibleCounter, _gpuVisibleCounter);
                cmd.SetComputeBufferParam(csSplatUtilities, argsKernel, Props.IndirectArgs, _gpuIndirectArgs);
                cmd.SetComputeIntParam(csSplatUtilities, Props.IndirectIndexCount, 6);
                cmd.DispatchCompute(csSplatUtilities, argsKernel, 1, 1, 1);
            }
            cmd.EndSample(ProfSort);
        }

        public void Update()
        {
            if (splatAsset != nextAsset)
            {
                _deferAssetFrames = DeferFrames;
            }

            if (_deferAssetFrames > 0)
            {
                _deferAssetFrames--;
                if (_deferAssetFrames == 0 && nextAsset != null)
                {
                    splatAsset = nextAsset;
                    _prevAsset = null;
                    _prevHash = default;
                }
            }

            var curHash = splatAsset ? splatAsset.dataHash : new Hash128();
            if (_prevAsset != splatAsset || _prevHash != curHash)
            {
                if (ResourcesAreSetUp)
                {
                    _prevAsset = splatAsset;
                    _prevHash = curHash;
                    _setupErrorLogged = false;
                    // OnEnable may have early-returned if the config appeared
                    // late (e.g. additively-loaded scene) — recover here.
                    EnsureSorterAndRegister();
                    CreateResourcesForAsset();
                }
                else if (!_setupErrorLogged)
                {
                    // Deliberately not latching _prevAsset/_prevHash so setup
                    // retries once the config becomes available.
                    _setupErrorLogged = true;
                    Debug.LogError($"{nameof(GaussianSplatRenderer)} requires a GaussianSplatConfig in the scene, or platform does not support compute shaders");
                }
            }
        }

        internal Bounds GetWorldBounds()
        {
            if (splatAsset == null)
                return new Bounds();
            var localCenter = (splatAsset.boundsMin + splatAsset.boundsMax) * 0.5f;
            var center = transform.TransformPoint(localCenter);
            var extents = (splatAsset.boundsMax - splatAsset.boundsMin) * 0.5f;
            var axisX = transform.TransformVector(extents.x, 0, 0);
            var axisY = transform.TransformVector(0, extents.y, 0);
            var axisZ = transform.TransformVector(0, 0, extents.z);
            extents.x = Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x);
            extents.y = Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y);
            extents.z = Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z);
            return new Bounds(center, extents * 2);
        }

        public void ActivateCamera(int index)
        {
            Camera mainCam = Camera.main;
            if (!mainCam)
                return;
            if (!splatAsset || splatAsset.cameras == null)
                return;

            var selfTr = transform;
            var camTr = mainCam.transform;
            var prevParent = camTr.parent;
            var cam = splatAsset.cameras[index];
            camTr.parent = selfTr;
            camTr.localPosition = cam.pos;
            camTr.localRotation = Quaternion.LookRotation(cam.axisZ, cam.axisY);
            camTr.parent = prevParent;
            camTr.localScale = Vector3.one;
#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(camTr);
#endif
        }

        // Forwarding methods for backward compatibility with Editor code
        public void UpdateEditCountsAndBounds() => EditManager.UpdateEditCountsAndBounds();
        public void EditStoreSelectionMouseDown() => EditManager.StoreSelectionMouseDown();
        public void EditStorePosMouseDown() => EditManager.StorePosMouseDown();
        public void EditStoreOtherMouseDown() => EditManager.StoreOtherMouseDown();
        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
            => EditManager.UpdateSelection(rectMin, rectMax, cam, subtract);
        public void EditTranslateSelection(Vector3 delta) => EditManager.TranslateSelection(delta);
        public void EditRotateSelection(Vector3 center, Matrix4x4 l2w, Matrix4x4 w2l, Quaternion rot)
            => EditManager.RotateSelection(center, l2w, w2l, rot);
        public void EditScaleSelection(Vector3 center, Matrix4x4 l2w, Matrix4x4 w2l, Vector3 scale)
            => EditManager.ScaleSelection(center, l2w, w2l, scale);
        public void EditDeleteSelected() => EditManager.DeleteSelected();
        public void EditSelectAll() => EditManager.SelectAll();
        public void EditDeselectAll() => EditManager.DeselectAll();
        public void EditInvertSelection() => EditManager.InvertSelection();
        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
            => EditManager.ExportData(dstData, bakeTransform);
        public void EditSetSplatCount(int count) => EditManager.SetSplatCount(count);
        public void EditCopySplatsInto(GaussianSplatRenderer dst, int srcStart, int dstStart, int count)
            => EditManager.CopySplatsInto(dst, srcStart, dstStart, count);
        public void EditCopySplats(Transform dstTransform, GraphicsBuffer dstPos, GraphicsBuffer dstOther,
            GraphicsBuffer dstSH, int dstSHDegree, Texture dstColor, GraphicsBuffer dstEditDeleted, int dstSize,
            int srcStart, int dstStart, int count)
            => EditManager.CopySplats(dstTransform, dstPos, dstOther, dstSH, dstSHDegree, dstColor, dstEditDeleted,
                dstSize, srcStart, dstStart, count);
        public GraphicsBuffer GpuEditDeleted => EditManager.GpuEditDeleted;
    }
}
