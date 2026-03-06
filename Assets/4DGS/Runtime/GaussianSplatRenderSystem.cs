using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatRenderSystem
    {
        // ReSharper disable MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        internal static readonly ProfilerMarker ProfDraw = new(ProfilerCategory.Render, "GaussianSplat.Draw", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker ProfCompose = new(ProfilerCategory.Render, "GaussianSplat.Compose", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker ProfCalcView = new(ProfilerCategory.Render, "GaussianSplat.CalcView", MarkerFlags.SampleGPU);
        // ReSharper restore MemberCanBePrivate.Global

        public static GaussianSplatRenderSystem instance => _instance ??= new GaussianSplatRenderSystem();
        static GaussianSplatRenderSystem _instance;

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> _splats = new();
        readonly HashSet<Camera> _cameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> _activeSplats = new();
        readonly MaterialPropertyBlock _globalMpb = new();
        readonly Dictionary<int, GlobalOrderGroupCache> _globalGroups = new();

        CommandBuffer _commandBuffer;
        GpuSorting _globalSorter;
        ComputeShader _globalSorterShader;
        int _globalFrameId;

        // ── Config ──────────────────────────────────────────────────────
        private GaussianSplatConfig _config;
        private bool _configWarningLogged;

        public GaussianSplatConfig Config
        {
            get
            {
                if (_config == null)
                {
                    _config = Object.FindFirstObjectByType<GaussianSplatConfig>();
                    if (_config == null)
                    {
                        if (!_configWarningLogged)
                        {
                            Debug.LogWarning("GaussianSplatConfig not found in scene. Gaussian splat rendering disabled.");
                            _configWarningLogged = true;
                        }
                    }
                    else
                    {
                        _configWarningLogged = false;
                    }
                }
                return _config;
            }
        }

        // ── Materials (created from Config shaders) ─────────────────────
        private Material _matSplats;
        private Material _matComposite;
        private Material _matDebugPoints;
        private Material _matDebugBoxes;
        private GaussianSplatConfig _materialSource;

        internal Material MatSplats => _matSplats;
        internal Material MatComposite => _matComposite;
        internal Material MatDebugPoints => _matDebugPoints;
        internal Material MatDebugBoxes => _matDebugBoxes;

        internal void EnsureMaterials()
        {
            if (_config == null) return;
            if (_matSplats != null && _materialSource == _config) return;

            Object.DestroyImmediate(_matSplats);
            Object.DestroyImmediate(_matComposite);
            Object.DestroyImmediate(_matDebugPoints);
            Object.DestroyImmediate(_matDebugBoxes);

            _matSplats = new Material(_config.ShaderSplats) { name = "GaussianSplats" };
            _matComposite = new Material(_config.ShaderComposite) { name = "GaussianClearDstAlpha" };
            _matDebugPoints = new Material(_config.ShaderDebugPoints) { name = "GaussianDebugPoints" };
            _matDebugBoxes = new Material(_config.ShaderDebugBoxes) { name = "GaussianDebugBoxes" };
            _materialSource = _config;
        }

        // ── Cross-camera GPU synchronisation ─────────────────────────
        GraphicsFence _lastRenderFence;
        bool _hasRenderFence;

        // ── Tile renderer ─────────────────────────────────────────────
        int _lastRetiredCleanupFrame = -1;
        private GaussianTileRenderer _tileRenderer;

        public RenderTargetIdentifier TileOutputTarget
        {
            get => _tileRenderer?.TileOutputTarget ?? default;
            set { _tileRenderer ??= new GaussianTileRenderer(); _tileRenderer.TileOutputTarget = value; }
        }

        public Vector2Int TileRenderSize
        {
            get => _tileRenderer?.TileRenderSize ?? default;
            set { _tileRenderer ??= new GaussianTileRenderer(); _tileRenderer.TileRenderSize = value; }
        }

        private static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        internal class GlobalOrderGroupCache
        {
            public int splatCapacity;
            public int groupSignature;
            public bool hasSortedKeys;
            public int lastUsedFrame;
            public GraphicsBuffer viewData;
            public GraphicsBuffer sortDistances;
            public GraphicsBuffer sortKeys;
            public GpuSorting.Args sorterArgs;

            // ── Tile-based rendering buffers ──────────────────────────────
            public const int MaxTilePairs = 16 * 1024 * 1024; // 16M (tile,splat) pairs
            public GraphicsBuffer tileKeys;         // sort key per pair: (tile_id<<16)|depth
            public GraphicsBuffer tileValues;       // splatId per pair
            public GraphicsBuffer tileRanges;       // uint2[numTiles]: (start,end) per tile
            public GraphicsBuffer tilePairCounter;  // 4-byte atomic counter
            public GpuSorting.Args tileSorterArgs;
            public int tileCountX, tileCountY;      // current tile grid dimensions
            public int tileRangesCapacity;          // allocated tile count (grow-only)
            public bool hasTileRanges;              // true after first tile sort+build
            public int lastTileCameraId;            // instance ID of last camera that built tile ranges
            public readonly Dictionary<int, uint> asyncTilePairCounts = new();
            public readonly HashSet<int> asyncReadbackPending = new();
            public readonly List<GraphicsBuffer> retiredTileRanges = new();

            public void DisposeRetiredBuffers()
            {
                foreach (var buf in retiredTileRanges)
                    buf.Dispose();
                retiredTileRanges.Clear();
            }

            public void Dispose()
            {
                viewData?.Dispose();
                sortDistances?.Dispose();
                sortKeys?.Dispose();
                sorterArgs.resources.Dispose();
                viewData = null;
                sortDistances = null;
                sortKeys = null;

                tileKeys?.Dispose();       tileKeys = null;
                tileValues?.Dispose();     tileValues = null;
                tileRanges?.Dispose();     tileRanges = null;
                tileRangesCapacity = 0;
                DisposeRetiredBuffers();
                tilePairCounter?.Dispose(); tilePairCounter = null;
                tileSorterArgs.resources.Dispose();
                hasTileRanges = false;
            }
        }

        private void DisposeGlobalResources()
        {
            foreach (var kvp in _globalGroups)
                kvp.Value.Dispose();
            _globalGroups.Clear();
            _globalSorter = null;
            _globalSorterShader = null;
            _tileRenderer = null;
            _hasRenderFence = false;

            Object.DestroyImmediate(_matSplats);
            Object.DestroyImmediate(_matComposite);
            Object.DestroyImmediate(_matDebugPoints);
            Object.DestroyImmediate(_matDebugBoxes);
            _matSplats = null;
            _matComposite = null;
            _matDebugPoints = null;
            _matDebugBoxes = null;
            _materialSource = null;
        }

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            if (_splats.Count == 0)
            {
                if (GraphicsSettings.currentRenderPipeline == null)
                    Camera.onPreCull += OnPreCullCamera;
            }

            _splats.Add(r, new MaterialPropertyBlock());
        }

        public void UnregisterSplat(GaussianSplatRenderer r)
        {
            if (!_splats.ContainsKey(r))
                return;
            _splats.Remove(r);
            if (_splats.Count == 0)
            {
                if (_cameraCommandBuffersDone != null)
                {
                    if (_commandBuffer != null)
                    {
                        foreach (var cam in _cameraCommandBuffersDone)
                        {
                            if (cam)
                                cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, _commandBuffer);
                        }
                    }
                    _cameraCommandBuffersDone.Clear();
                }

                _activeSplats.Clear();
                DisposeGlobalResources();
                _commandBuffer?.Dispose();
                _commandBuffer = null;
                _config = null;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;

            var config = Config;
            if (config == null || !config.ResourcesValid)
                return false;

            EnsureMaterials();

            // gather all active & valid splat objects
            _activeSplats.Clear();
            foreach (var kvp in _splats)
            {
                var gs = kvp.Key;
                if (gs == null || !gs.isActiveAndEnabled || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                _activeSplats.Add((kvp.Key, kvp.Value));
            }
            if (_activeSplats.Count == 0)
                return false;

            // sort them by order and depth from camera
            var camTr = cam.transform;
            _activeSplats.Sort((a, b) =>
            {
                var orderA = a.Item1.renderOrder;
                var orderB = b.Item1.renderOrder;
                if (orderA != orderB)
                    return orderB.CompareTo(orderA);
                var trA = a.Item1.transform;
                var trB = b.Item1.transform;
                var posA = camTr.InverseTransformPoint(trA.position);
                var posB = camTr.InverseTransformPoint(trB.position);
                return posA.z.CompareTo(posB.z);
            });

            return true;
        }

        public static int MaxTilePairsCapacity => GlobalOrderGroupCache.MaxTilePairs;

        // Latest tile pair count from async readback (for diagnostics / Inspector).
        public uint LastTilePairCount => _tileRenderer?.GetLastTilePairCount(_globalGroups) ?? 0;

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            if (_hasRenderFence)
                cmb.WaitOnAsyncGraphicsFence(_lastRenderFence);

            Material result = CanUseGlobalSortPath()
                ? SortAndRenderSplatsGlobal(cam, cmb)
                : SortAndRenderSplatsPerObject(cam, cmb);

            _lastRenderFence = cmb.CreateAsyncGraphicsFence();
            _hasRenderFence = true;

            return result;
        }

        private bool CanUseGlobalSortPath()
        {
            if (_config.renderMode != GaussianSplatRenderMode.Splats)
                return false;
            foreach (var kvp in _activeSplats)
            {
                var gs = kvp.Item1;
                if (!gs.SupportsGlobalSortPath())
                    return false;
            }
            return true;
        }

        private bool EnsureGlobalSorter()
        {
            var cs = _config?.CsSplatUtilities;
            if (cs == null) return false;
            if (_globalSorter == null || _globalSorterShader != cs)
            {
                _globalSorter = new GpuSorting(cs);
                _globalSorterShader = cs;
            }
            return _globalSorter != null && _globalSorter.Valid;
        }

        private bool EnsureGlobalGroupCache(int renderOrder, int splatCount, out GlobalOrderGroupCache cache)
        {
            cache = null;
            if (!EnsureGlobalSorter() || splatCount <= 0)
                return false;

            if (!_globalGroups.TryGetValue(renderOrder, out cache))
            {
                cache = new GlobalOrderGroupCache();
                _globalGroups.Add(renderOrder, cache);
            }

            if (cache.viewData == null || cache.splatCapacity < splatCount)
            {
                cache.viewData?.Dispose();
                cache.sortDistances?.Dispose();
                cache.sortKeys?.Dispose();
                cache.viewData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, GaussianSplatRenderer.GpuViewDataSize)
                {
                    name = $"GaussianGlobalViewData_{renderOrder}"
                };
                cache.sortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, 4)
                {
                    name = $"GaussianGlobalSortDistances_{renderOrder}"
                };
                cache.sortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, 4)
                {
                    name = $"GaussianGlobalSortIndices_{renderOrder}"
                };
                cache.splatCapacity = splatCount;
                cache.hasSortedKeys = false;
            }

            if (cache.sorterArgs.resources.altBuffer == null || cache.sorterArgs.count < (uint)splatCount)
            {
                cache.sorterArgs.resources.Dispose();
                cache.sorterArgs.resources = GpuSorting.SupportResources.Load((uint)splatCount);
            }

            cache.sorterArgs.inputKeys = cache.sortDistances;
            cache.sorterArgs.inputValues = cache.sortKeys;
            cache.sorterArgs.count = (uint)splatCount;
            cache.lastUsedFrame = _globalFrameId;
            return true;
        }

        private void ReleaseUnusedGlobalGroups(int activeFrameId)
        {
            if (_globalGroups.Count == 0)
                return;
            List<int> stale = null;
            foreach (var kvp in _globalGroups)
            {
                if (kvp.Value.lastUsedFrame == activeFrameId)
                    continue;
                stale ??= new List<int>();
                stale.Add(kvp.Key);
            }
            if (stale == null)
                return;
            foreach (var key in stale)
            {
                _globalGroups[key].Dispose();
                _globalGroups.Remove(key);
            }
        }

        Material SortAndRenderSplatsGlobal(Camera cam, CommandBuffer cmb)
        {
            int unityFrame = Time.frameCount;
            if (_lastRetiredCleanupFrame != unityFrame)
            {
                _lastRetiredCleanupFrame = unityFrame;
                foreach (var kvp in _globalGroups)
                    kvp.Value.DisposeRetiredBuffers();
            }

            ++_globalFrameId;
            int activeFrameId = _globalFrameId;
            Material matComposite = null;
            bool hasRenderedAGroup = false;
            int groupStart = 0;
            while (groupStart < _activeSplats.Count)
            {
                int order = _activeSplats[groupStart].Item1.renderOrder;
                int groupEnd = groupStart + 1;
                int groupSplatCount = _activeSplats[groupStart].Item1.EffectiveSplatCount;
                while (groupEnd < _activeSplats.Count && _activeSplats[groupEnd].Item1.renderOrder == order)
                {
                    groupSplatCount += _activeSplats[groupEnd].Item1.EffectiveSplatCount;
                    ++groupEnd;
                }

                var reference = _activeSplats[groupStart].Item1;
                if (!EnsureGlobalGroupCache(order, groupSplatCount, out var groupCache))
                {
                    if (!hasRenderedAGroup)
                    {
                        ReleaseUnusedGlobalGroups(activeFrameId);
                        return SortAndRenderSplatsPerObject(cam, cmb);
                    }
                    groupStart = groupEnd;
                    continue;
                }

                int groupSignature = 17;
                bool groupSortNeeded = !groupCache.hasSortedKeys;
                for (int i = groupStart; i < groupEnd; ++i)
                {
                    var gs = _activeSplats[i].Item1;
                    groupSignature = groupSignature * 31 + gs.GetInstanceID();
                    groupSignature = groupSignature * 31 + gs.EffectiveSplatCount;
                    int nth = Mathf.Max(1, gs.sortNthFrame);
                    if (gs.FrameCounter % nth == 0)
                        groupSortNeeded = true;
                }
                if (groupCache.groupSignature != groupSignature)
                {
                    groupCache.hasSortedKeys = false;
                    groupSortNeeded = true;
                }

                Material groupDisplayMat = null;
                int dstOffset = 0;
                bool groupValid = true;
                for (int i = groupStart; i < groupEnd; ++i)
                {
                    var gs = _activeSplats[i].Item1;
                    matComposite = _matComposite;
                    groupDisplayMat ??= _matSplats;
                    if (_matSplats == null)
                    {
                        groupValid = false;
                        break;
                    }

                    cmb.BeginSample(ProfCalcView);
                    bool calcSuccess = gs.CalcViewData(cmb, cam);
                    cmb.EndSample(ProfCalcView);
                    if (!calcSuccess)
                    {
                        groupValid = false;
                        break;
                    }

                    bool copySuccess = gs.CopyViewDataAndDistances(cmb, cam, gs.transform.localToWorldMatrix,
                        groupCache.viewData, groupCache.sortDistances, groupCache.sortKeys,
                        0, dstOffset, gs.EffectiveSplatCount, groupSortNeeded || !groupCache.hasSortedKeys);
                    if (!copySuccess)
                    {
                        groupValid = false;
                        break;
                    }

                    dstOffset += gs.EffectiveSplatCount;
                    ++gs.FrameCounter;
                }

                if (!groupValid || groupDisplayMat == null || dstOffset == 0)
                {
                    if (!hasRenderedAGroup)
                    {
                        ReleaseUnusedGlobalGroups(activeFrameId);
                        return SortAndRenderSplatsPerObject(cam, cmb);
                    }
                    groupStart = groupEnd;
                    continue;
                }

                groupCache.groupSignature = groupSignature;

                cmb.BeginSample(ProfDraw);
                // ── Tile-based rendering path ──────────────────────────
                bool usedTile = false;
                int tileW = cam.pixelWidth;
                int tileH = cam.pixelHeight;
                _tileRenderer ??= new GaussianTileRenderer();
                if (_config.useTileRenderer &&
                    _tileRenderer.EnsureResources(_config, groupCache, tileW, tileH))
                {
                    _tileRenderer.Dispatch(cmb, cam, groupCache, dstOffset, groupSortNeeded, tileW, tileH);
                    usedTile = true;
                }

                if (!usedTile && (groupSortNeeded || !groupCache.hasSortedKeys))
                {
                    groupCache.sorterArgs.count = (uint)dstOffset;
                    _globalSorter.Dispatch(cmb, groupCache.sorterArgs);
                    groupCache.hasSortedKeys = true;
                }

                // ── Fallback: traditional DrawProcedural ───────────────
                if (!usedTile)
                {
                    _globalMpb.Clear();
                    _globalMpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, groupCache.viewData);
                    _globalMpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, groupCache.sortKeys);
                    cmb.DrawProcedural(reference.GpuIndexBuffer, Matrix4x4.identity,
                        groupDisplayMat, 0, MeshTopology.Triangles, 6, dstOffset, _globalMpb);
                }
                cmb.EndSample(ProfDraw);

                hasRenderedAGroup = true;
                groupStart = groupEnd;
            }
            ReleaseUnusedGlobalGroups(activeFrameId);
            return matComposite;
        }

        Material SortAndRenderSplatsPerObject(Camera cam, CommandBuffer cmb)
        {
            Material matComposite = _matComposite;
            foreach (var kvp in _activeSplats)
            {
                var gs = kvp.Item1;
                var mpb = kvp.Item2;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                if (gs.FrameCounter % gs.sortNthFrame == 0)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs.FrameCounter;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = _config.renderMode switch
                {
                    GaussianSplatRenderMode.DebugPoints => _matDebugPoints,
                    GaussianSplatRenderMode.DebugPointIndices => _matDebugPoints,
                    GaussianSplatRenderMode.DebugBoxes => _matDebugBoxes,
                    GaussianSplatRenderMode.DebugChunkBounds => _matDebugBoxes,
                    _ => _matSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.GpuChunksBuffer);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.GpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.splatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.opacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, _config.pointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.shOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.shOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, _config.renderMode == GaussianSplatRenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, _config.renderMode == GaussianSplatRenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(ProfCalcView);
                bool calcSuccess = gs.CalcViewData(cmb, cam);
                cmb.EndSample(ProfCalcView);
                if (!calcSuccess)
                    continue;

                // draw
                int indexCount = 6;
                int instanceCount = gs.EffectiveSplatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (_config.renderMode is GaussianSplatRenderMode.DebugBoxes or GaussianSplatRenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (_config.renderMode == GaussianSplatRenderMode.DebugChunkBounds)
                    instanceCount = gs.GpuChunksValid ? gs.GpuChunksBuffer.count : 0;

                cmb.BeginSample(ProfDraw);
                cmb.DrawProcedural(gs.GpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
                cmb.EndSample(ProfDraw);
            }
            return matComposite;
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        // ReSharper disable once UnusedMethodReturnValue.Global - used by HDRP/URP features that are not always compiled
        public CommandBuffer InitialClearCmdBuffer(Camera cam)
        {
            _commandBuffer ??= new CommandBuffer {name = "RenderGaussianSplats"};
            if (GraphicsSettings.currentRenderPipeline == null && cam != null && !_cameraCommandBuffersDone.Contains(cam))
            {
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, _commandBuffer);
                _cameraCommandBuffersDone.Add(cam);
            }

            // get render target for all splats
            _commandBuffer.Clear();
            return _commandBuffer;
        }

        private void OnPreCullCamera(Camera cam)
        {
            if (!GatherSplatsForCamera(cam))
                return;

            InitialClearCmdBuffer(cam);

            _commandBuffer.GetTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT, -1, -1, 0, FilterMode.Point, GraphicsFormat.R16G16B16A16_SFloat);
            _commandBuffer.SetRenderTarget(GaussianSplatRenderer.Props.GaussianSplatRT, BuiltinRenderTextureType.CurrentActive);
            _commandBuffer.ClearRenderTarget(RTClearFlags.Color, new Color(0, 0, 0, 0), 0, 0);

            _commandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            Material matComposite = SortAndRenderSplats(cam, _commandBuffer);

            if (matComposite != null)
            {
                _commandBuffer.BeginSample(ProfCompose);
                _commandBuffer.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
                _commandBuffer.DrawProcedural(Matrix4x4.identity, matComposite, 0, MeshTopology.Triangles, 3, 1);
                _commandBuffer.EndSample(ProfCompose);
            }
            _commandBuffer.ReleaseTemporaryRT(GaussianSplatRenderer.Props.GaussianSplatRT);
        }
    }
}
