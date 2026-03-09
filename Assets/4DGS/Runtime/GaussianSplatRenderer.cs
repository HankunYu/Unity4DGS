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
    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        [FormerlySerializedAs("m_Asset")] public GaussianSplatAsset splatAsset;
        [FormerlySerializedAs("m_NextAsset")] public GaussianSplatAsset nextAsset;

        [Tooltip("Rendering order compared to other splats. Within same order splats are sorted by distance. Higher order splats render 'on top of' lower order splats.")]
        [FormerlySerializedAs("m_RenderOrder")] public int renderOrder;
        [Range(0.1f, 2.0f)] [Tooltip("Additional scaling factor for the splats")]
        [FormerlySerializedAs("m_SplatScale")] public float splatScale = 1.0f;
        [Range(0.05f, 20.0f)]
        [Tooltip("Additional scaling factor for opacity")]
        [FormerlySerializedAs("m_OpacityScale")] public float opacityScale = 1.0f;
        [Range(0, 3)] [Tooltip("Spherical Harmonics order to use")]
        [FormerlySerializedAs("m_SHOrder")] public int shOrder = 3;
        [Tooltip("Show only Spherical Harmonics contribution, using gray color")]
        [FormerlySerializedAs("m_SHOnly")] public bool shOnly;
        [Range(1,30)] [Tooltip("Sort splats only every N frames")]
        [FormerlySerializedAs("m_SortNthFrame")] public int sortNthFrame = 1;

        public GaussianCutoutManager CutoutManager => GetComponent<GaussianCutoutManager>();

        // Set by GaussianCutoutManager: cutout data for CalcViewData
        private GraphicsBuffer _cutoutBuffer;
        private int _cutoutCount;

        int _splatCount; // initially same as asset splat count, but editing can change this
        private GraphicsBuffer _gpuSortDistances;
        private GraphicsBuffer _gpuSortKeys;
        internal GraphicsBuffer _gpuPosData;
        internal GraphicsBuffer _gpuOtherData;
        internal GraphicsBuffer _gpuSHData;

        // Set by GaussianAnimator: per-splat animation output (3 float4s per splat)
        private GraphicsBuffer _animOutputBuffer;
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

        private GaussianSplatEditManager _editManager;

        internal GaussianSplatEditManager EditManager
            => _editManager ??= new GaussianSplatEditManager(this);

        private GpuSorting _sorter;
        private GpuSorting.Args _sorterArgs;
        private readonly Dictionary<string, int> _kernelIndexCache = new();

        private int _frameCounter;
        GaussianSplatAsset _prevAsset;
        private int _deferAssetFrames = 0;
        private const int DeferFrames = 1;
        Hash128 _prevHash;
        bool _registered;

        static readonly ProfilerMarker ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

        // Forwarding property: compute shader comes from global Config
        internal ComputeShader csSplatUtilities => GaussianSplatRenderSystem.instance.Config?.CsSplatUtilities;

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
            public static readonly int SplatCount = Shader.PropertyToID("_SplatCount");
            public static readonly int SHOrder = Shader.PropertyToID("_SHOrder");
            public static readonly int SHOnly = Shader.PropertyToID("_SHOnly");
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
            public static readonly int MatrixObjectToWorld = Shader.PropertyToID("_MatrixObjectToWorld");
            public static readonly int MatrixWorldToObject = Shader.PropertyToID("_MatrixWorldToObject");
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
            public static readonly int MorphedData = Shader.PropertyToID("_MorphedData");
            public static readonly int MorphDataValid = Shader.PropertyToID("_MorphDataValid");
            public static readonly int MorphWeight = Shader.PropertyToID("_MorphWeight");
            public static readonly int MorphSHData = Shader.PropertyToID("_MorphSHData");
            public static readonly int SrcSplatCount = Shader.PropertyToID("_SrcSplatCount");
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
        internal GraphicsBuffer GpuIndexBuffer => _gpuIndexBuffer;
        internal GraphicsBuffer GpuView => _gpuView;
        internal GraphicsBuffer AnimOutputBuffer => _animOutputBuffer;
        internal int FrameCounter { get => _frameCounter; set => _frameCounter = value; }

        internal void SetMorphData(GraphicsBuffer morphedData, int splatCount, int valid, float weight = 0f, GraphicsBuffer morphSH = null)
        {
            _morphedDataBuffer = morphedData;
            _morphSHBuffer = morphSH;
            _morphedSplatCount = splatCount;
            _morphDataValid = valid;
            _morphWeight = weight;

            int needed = EffectiveSplatCount;
            if (_gpuView != null && _gpuView.count < needed)
            {
                _gpuView.Dispose();
                _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, needed, GpuViewDataSize);
            }
            if (_gpuSortDistances != null && _gpuSortDistances.count < needed)
            {
                InitSortBuffers(needed);
            }
        }

        internal void SetAnimationOutput(GraphicsBuffer animBuffer)
        {
            _animOutputBuffer = animBuffer;
        }

        internal void ClearAnimationOutput()
        {
            _animOutputBuffer = null;
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
        };

        const string CopyViewDataAndDistancesKernelName = "CSCopyViewDataAndDistances";

        public bool HasValidAsset =>
            splatAsset != null &&
            splatAsset.splatCount > 0 &&
            splatAsset.formatVersion == GaussianSplatAsset.CurrentVersion &&
            splatAsset.posData != null &&
            splatAsset.otherData != null &&
            splatAsset.shData != null &&
            splatAsset.colorData != null;
        public bool HasValidRenderSetup => _gpuPosData != null && _gpuOtherData != null && _gpuChunks != null;

        internal const int GpuViewDataSize = 40;

        private bool ResourcesAreSetUp => csSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void InitResourcesForAssets(long posDataMaxSize, long otherDataMaxSize, long shDataMaxSize, long chunkDataMaxSize, long splatCountMaxSize)
        {
            _gpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(posDataMaxSize / 4), 4) { name = "GaussianPosData" };
            _gpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(otherDataMaxSize / 4), 4) { name = "GaussianOtherData" };
            _gpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(shDataMaxSize / 4), 4) { name = "GaussianSHData" };
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int)(chunkDataMaxSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
            else
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
            _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, (int)splatCountMaxSize, GpuViewDataSize);
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
            _gpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            _gpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int)(asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            _gpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int)(asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
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
            _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatAsset.splatCount, GpuViewDataSize);
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
        private void CreateResourcesForAsset()
        {
            if (!HasValidAsset)
                return;
            if (_gpuPosData == null)
            {
                InitResourcesForAsset();
            }

            _splatCount = asset.splatCount;
            _gpuPosData.SetData(asset.posData.GetData<uint>());
            _gpuOtherData.SetData(asset.otherData.GetData<uint>());
            _gpuSHData.SetData(asset.shData.GetData<uint>());

            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            if (_gpuColorData != null)
                DestroyImmediate(_gpuColorData);
            _gpuColorData = tex;
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

        private void CreateResourcesForAssetV0()
        {
            if (!HasValidAsset)
                return;

            _splatCount = asset.splatCount;
            _gpuPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.posData.dataSize / 4), 4) { name = "GaussianPosData" };
            _gpuPosData.SetData(asset.posData.GetData<uint>());
            _gpuOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, (int) (asset.otherData.dataSize / 4), 4) { name = "GaussianOtherData" };
            _gpuOtherData.SetData(asset.otherData.GetData<uint>());
            _gpuSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, (int) (asset.shData.dataSize / 4), 4) { name = "GaussianSHData" };
            _gpuSHData.SetData(asset.shData.GetData<uint>());
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(asset.splatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            if (_gpuColorData != null)
                DestroyImmediate(_gpuColorData);
            _gpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    (int) (asset.chunkData.dataSize / UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()),
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                _gpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                _gpuChunksValid = true;
            }
            else
            {
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                _gpuChunksValid = false;
            }

            _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatAsset.splatCount, GpuViewDataSize);
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

            InitSortBuffers(splatCount);
        }

        internal void InitSortBuffers(int count)
        {
            _gpuSortDistances?.Dispose();
            _gpuSortKeys?.Dispose();
            _sorterArgs.resources.Dispose();

            EnsureSorterAndRegister();

            _gpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortDistances" };
            _gpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, 4) { name = "GaussianSplatSortIndices" };

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
            if (_sorter != null && _sorter.Valid)
                _sorterArgs.resources = GpuSorting.SupportResources.Load((uint)count);
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
            catch
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
            {
                _sorter = new GpuSorting(csSplatUtilities);
            }

            if (!_registered && ResourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                _registered = true;
            }
        }

        private void OnEnable()
        {
            _frameCounter = 0;
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
            ComputeShader cs = csSplatUtilities;
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
            uint format = (uint)splatAsset.posFormat | ((uint)splatAsset.scaleFormat << 8) | ((uint)splatAsset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
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

            // Morph data binding
            bool hasMorph = _morphedDataBuffer != null && _morphDataValid != 0;
            bool hasMorphSH = hasMorph && _morphSHBuffer != null;
            cmb.SetComputeIntParam(cs, Props.MorphDataValid, hasMorphSH ? 2 : (hasMorph ? 1 : 0));
            cmb.SetComputeFloatParam(cs, Props.MorphWeight, hasMorph ? _morphWeight : 0f);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.MorphedData, hasMorph ? _morphedDataBuffer : _gpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.MorphSHData, hasMorphSH ? _morphSHBuffer : _gpuPosData);
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
            uint format = (uint)splatAsset.posFormat | ((uint)splatAsset.scaleFormat << 8) | ((uint)splatAsset.shFormat << 16);
            mat.SetInteger(Props.SplatFormat, (int)format);
            mat.SetInteger(Props.SplatCount, _splatCount);
            mat.SetInteger(Props.SplatChunkCount, _gpuChunksValid ? _gpuChunks.count : 0);
        }

        private static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        private void DisposeResourcesForAsset()
        {
            DestroyImmediate(_gpuColorData);

            DisposeBuffer(ref _gpuPosData);
            DisposeBuffer(ref _gpuOtherData);
            DisposeBuffer(ref _gpuSHData);
            DisposeBuffer(ref _gpuChunks);

            DisposeBuffer(ref _gpuView);
            DisposeBuffer(ref _gpuIndexBuffer);
            DisposeBuffer(ref _gpuSortDistances);
            DisposeBuffer(ref _gpuSortKeys);

            _sorterArgs.resources.Dispose();

            _splatCount = 0;
            _gpuChunksValid = false;
        }

        private void OnDisable()
        {
            _editManager?.Dispose();
            _editManager = null;
            DisposeResourcesForAsset();
            _kernelIndexCache.Clear();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            _registered = false;
        }

        internal bool CalcViewData(CommandBuffer cmb, Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;

            var tr = transform;

            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            int eyeW = XRSettings.eyeTextureWidth, eyeH = XRSettings.eyeTextureHeight;
            Vector4 screenPar = new Vector4(eyeW != 0 ? eyeW : screenW, eyeH != 0 ? eyeH : screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            if (!SetAssetDataOnCS(cmb, KernelIndices.CalcViewData, out int kernelIndex))
                return false;

            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixVP, GL.GetGPUProjectionMatrix(cam.projectionMatrix, false) * matView);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(csSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(csSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);
            cmb.SetComputeFloatParam(csSplatUtilities, Props.SplatScale, splatScale);
            cmb.SetComputeFloatParam(csSplatUtilities, Props.SplatOpacityScale, opacityScale);
            cmb.SetComputeIntParam(csSplatUtilities, Props.SHOrder, shOrder);
            cmb.SetComputeIntParam(csSplatUtilities, Props.SHOnly, shOnly ? 1 : 0);

            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            int dispatchCount = EffectiveSplatCount;
            cmb.DispatchCompute(csSplatUtilities, kernelIndex, (dispatchCount + (int)gsX - 1)/(int)gsX, 1, 1);
            return true;
        }

        internal bool SupportsGlobalSortPath()
        {
            return csSplatUtilities != null &&
                   TryFindSupportedKernel(KernelIndices.CalcViewData, out _) &&
                   TryFindSupportedKernel(CopyViewDataAndDistancesKernelName, out _);
        }

        internal bool CopyViewDataAndDistances(CommandBuffer cmb, Camera cam, Matrix4x4 matrix,
            GraphicsBuffer dstViewData, GraphicsBuffer dstSortDistances, GraphicsBuffer dstSortKeys,
            int srcStartIndex, int dstStartIndex, int copyCount, bool writeSortKeys)
        {
            if (copyCount <= 0)
                return true;
            if (!TryFindSupportedKernel(CopyViewDataAndDistancesKernelName, out int kernelIndex))
                return false;
            SetAssetDataOnCS(cmb, kernelIndex);

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.CopyDstViewData, dstViewData);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.CopyDstSortDistances, dstSortDistances);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.CopyDstSortKeys, dstSortKeys);
            cmb.SetComputeIntParam(csSplatUtilities, Props.CopySrcStartIndex, srcStartIndex);
            cmb.SetComputeIntParam(csSplatUtilities, Props.CopyDstStartIndex, dstStartIndex);
            cmb.SetComputeIntParam(csSplatUtilities, Props.CopyKernelCount, copyCount);
            cmb.SetComputeIntParam(csSplatUtilities, Props.CopyWriteKeys, writeSortKeys ? 1 : 0);

            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            cmb.DispatchCompute(csSplatUtilities, kernelIndex, (copyCount + (int)gsX - 1)/(int)gsX, 1, 1);
            return true;
        }

        internal void SortPoints(CommandBuffer cmd, Camera cam, Matrix4x4 matrix)
        {
            if (cam.cameraType == CameraType.Preview)
                return;
            if (!TryFindSupportedKernel(KernelIndices.CalcDistances, out int kernelIndex))
                return;

            Matrix4x4 worldToCamMatrix = cam.worldToCameraMatrix;
            worldToCamMatrix.m20 *= -1;
            worldToCamMatrix.m21 *= -1;
            worldToCamMatrix.m22 *= -1;

            cmd.BeginSample(ProfSort);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatSortDistances, _gpuSortDistances);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatSortKeys, _gpuSortKeys);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatChunks, _gpuChunks);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatPos, _gpuPosData);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatFormat, (int)splatAsset.posFormat);
            cmd.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatCount, EffectiveSplatCount);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatChunkCount, _gpuChunksValid ? _gpuChunks.count : 0);

            bool hasMorph = _morphedDataBuffer != null && _morphDataValid != 0;
            cmd.SetComputeIntParam(csSplatUtilities, Props.MorphDataValid, hasMorph ? 1 : 0);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.MorphedData, hasMorph ? _morphedDataBuffer : _gpuSortDistances);

            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            cmd.DispatchCompute(csSplatUtilities, kernelIndex, (EffectiveSplatCount + (int)gsX - 1)/(int)gsX, 1, 1);

            EnsureSorterAndRegister();
            _sorterArgs.count = (uint)EffectiveSplatCount;
            _sorter.Dispatch(cmd, _sorterArgs);
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
                _prevAsset = splatAsset;
                _prevHash = curHash;
                if (ResourcesAreSetUp)
                {
                    CreateResourcesForAsset();
                }
                else
                {
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
            GraphicsBuffer dstSH, Texture dstColor, GraphicsBuffer dstEditDeleted, int dstSize,
            int srcStart, int dstStart, int count)
            => EditManager.CopySplats(dstTransform, dstPos, dstOther, dstSH, dstColor, dstEditDeleted,
                dstSize, srcStart, dstStart, count);
        public GraphicsBuffer GpuEditDeleted => EditManager.GpuEditDeleted;
    }
}
