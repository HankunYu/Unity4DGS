// SPDX-License-Identifier: MIT

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
    class GaussianSplatRenderSystem
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

        // ── Tile renderer ─────────────────────────────────────────────
        GpuSorting _tileSorter;
        ComputeShader _tileCs;
        // Set by URP/HDRP feature before SortAndRenderSplats; tile renderer
        // writes directly here (avoids SetRenderTarget/Blit inside render graph).
        public RenderTargetIdentifier TileOutputTarget;
        int _kernelTileAssign  = -1;
        int _kernelBuildRanges = -1;
        int _kernelRenderTiles = -1;

        static class TileProps
        {
            public static readonly int SplatCount      = Shader.PropertyToID("_SplatCount");
            public static readonly int TileCountX      = Shader.PropertyToID("_TileCountX");
            public static readonly int TileCountY      = Shader.PropertyToID("_TileCountY");
            public static readonly int MaxTilePairs    = Shader.PropertyToID("_MaxTilePairs");
            public static readonly int ScreenParams    = Shader.PropertyToID("_ScreenParams");
            public static readonly int SplatViewData   = Shader.PropertyToID("_SplatViewData");
            public static readonly int TilePairCounter = Shader.PropertyToID("_TilePairCounter");
            public static readonly int TileKeys        = Shader.PropertyToID("_TileKeys");
            public static readonly int TileValues      = Shader.PropertyToID("_TileValues");
            public static readonly int TilePairCount   = Shader.PropertyToID("_TilePairCount");
            public static readonly int SortedTileKeys  = Shader.PropertyToID("_SortedTileKeys");
            public static readonly int TileRanges      = Shader.PropertyToID("_TileRanges");
            public static readonly int SortedTileValues= Shader.PropertyToID("_SortedTileValues");
            public static readonly int TileRangesRead  = Shader.PropertyToID("_TileRangesRead");
            public static readonly int OutputTexture   = Shader.PropertyToID("_OutputTexture");
        }

        private static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        class GlobalOrderGroupCache
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
            public const int MaxTilePairs = 8 * 1024 * 1024; // 8M (tile,splat) pairs
            public GraphicsBuffer tileKeys;         // sort key per pair: (tile_id<<18)|depth
            public GraphicsBuffer tileValues;       // splatId per pair
            public GraphicsBuffer tileRanges;       // uint2[numTiles]: (start,end) per tile
            public GraphicsBuffer tilePairCounter;  // 4-byte atomic counter
            public GpuSorting.Args tileSorterArgs;
            public int tileCountX, tileCountY;      // current tile grid dimensions

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
                tilePairCounter?.Dispose(); tilePairCounter = null;
                tileSorterArgs.resources.Dispose();
            }
        }

        private void DisposeGlobalResources()
        {
            foreach (var kvp in _globalGroups)
                kvp.Value.Dispose();
            _globalGroups.Clear();
            _globalSorter = null;
            _globalSorterShader = null;
            _tileSorter = null;
            _tileCs = null;
            _kernelTileAssign = _kernelBuildRanges = _kernelRenderTiles = -1;
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
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public bool GatherSplatsForCamera(Camera cam)
        {
            if (cam.cameraType == CameraType.Preview)
                return false;
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

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            if (CanUseGlobalSortPath())
                return SortAndRenderSplatsGlobal(cam, cmb);
            return SortAndRenderSplatsPerObject(cam, cmb);
        }

        private bool CanUseGlobalSortPath()
        {
            foreach (var kvp in _activeSplats)
            {
                var gs = kvp.Item1;
                gs.EnsureMaterials();
                if (gs.renderMode != GaussianSplatRenderer.RenderMode.Splats)
                    return false;
                if (!gs.SupportsGlobalSortPath())
                    return false;
            }
            return true;
        }

        private bool EnsureGlobalSorter(GaussianSplatRenderer reference)
        {
            if (reference == null || reference.csSplatUtilities == null)
                return false;
            if (_globalSorter == null || _globalSorterShader != reference.csSplatUtilities)
            {
                _globalSorter = new GpuSorting(reference.csSplatUtilities);
                _globalSorterShader = reference.csSplatUtilities;
            }
            return _globalSorter != null && _globalSorter.Valid;
        }

        private bool EnsureGlobalGroupCache(int renderOrder, GaussianSplatRenderer reference, int splatCount, out GlobalOrderGroupCache cache)
        {
            cache = null;
            if (!EnsureGlobalSorter(reference) || splatCount <= 0)
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

        // ── Tile rendering helpers ────────────────────────────────────────

        static readonly uint[] s_ZeroUint = new uint[1];
        uint[] _tileRangesClearBuf;  // reused per-frame clear buffer for tileRanges

        bool EnsureTileResources(GaussianSplatRenderer reference, GlobalOrderGroupCache cache,
                                 int screenW, int screenH, int splatCount)
        {
            if (_tileCs == null)
            {
                // Prefer Inspector-assigned shader; fall back to Resources auto-load
                _tileCs = reference.csTileRender
                    ?? Resources.Load<ComputeShader>("GaussianTileRender");
                if (_tileCs == null) return false;
                _kernelTileAssign  = _tileCs.FindKernel("CSGaussianTileAssign");
                _kernelBuildRanges = _tileCs.FindKernel("CSBuildTileRanges");
                _kernelRenderTiles = _tileCs.FindKernel("CSRenderTiles");
            }
            if (_tileCs == null) return false;

            int tileX = (screenW + 15) / 16;
            int tileY = (screenH + 15) / 16;
            int numTiles = tileX * tileY;

            // Allocate / reallocate tile pair buffers when splat count changes
            if (cache.tileKeys == null || cache.tileKeys.count < GlobalOrderGroupCache.MaxTilePairs)
            {
                cache.tileKeys?.Dispose();
                cache.tileValues?.Dispose();
                cache.tileKeys   = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    GlobalOrderGroupCache.MaxTilePairs, 4) { name = "TileKeys" };
                cache.tileValues = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    GlobalOrderGroupCache.MaxTilePairs, 4) { name = "TileValues" };
            }

            // Reallocate tile ranges when screen resolution changes
            if (cache.tileRanges == null || cache.tileCountX != tileX || cache.tileCountY != tileY)
            {
                cache.tileRanges?.Dispose();
                cache.tileRanges = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    numTiles, 8) { name = "TileRanges" };   // uint2 per tile
                cache.tileCountX = tileX;
                cache.tileCountY = tileY;
            }

            if (cache.tilePairCounter == null)
                cache.tilePairCounter = new GraphicsBuffer(
                    GraphicsBuffer.Target.Raw, 1, 4) { name = "TilePairCounter" };

            // Sorter for tile pairs
            if (cache.tileSorterArgs.resources.altBuffer == null ||
                cache.tileSorterArgs.count < (uint)GlobalOrderGroupCache.MaxTilePairs)
            {
                cache.tileSorterArgs.resources.Dispose();
                cache.tileSorterArgs.resources =
                    GpuSorting.SupportResources.Load((uint)GlobalOrderGroupCache.MaxTilePairs);
            }
            // GpuSorting requires the csSplatUtilities shader (contains radix sort kernels)
            _tileSorter ??= new GpuSorting(reference.csSplatUtilities);

            return true;
        }

        void DispatchTileRender(CommandBuffer cmb, Camera cam,
                                GlobalOrderGroupCache cache, int splatCount, bool sortNeeded)
        {
            int screenW = cam.pixelWidth;
            int screenH = cam.pixelHeight;
            int tileX   = cache.tileCountX;
            int tileY   = cache.tileCountY;
            int numTiles = tileX * tileY;

            // ── On hit frames skip assign/sort/build; only re-render ─────
            if (!sortNeeded && cache.tileRanges != null)
                goto renderOnly;

            // ── Read PREVIOUS frame's pair count (before submitting GPU work) ─
            // GetData stalls until the GPU counter buffer is ready.
            // Since this runs at the start of frame N's CB recording,
            // the GPU has already finished frame N-1's work, so the counter
            // holds frame N-1's result. We use it to sort frame N's pairs
            // (1-frame lag — acceptable for stable scenes).
            var counterBuf = new uint[1];
            cache.tilePairCounter.GetData(counterBuf);
            uint sortCount = counterBuf[0];
            if (sortCount == 0)
                sortCount = (uint)(splatCount * 2);  // frame-0 estimate
            sortCount = (uint)Mathf.Min((int)sortCount, GlobalOrderGroupCache.MaxTilePairs);

            // ── Clear counter and tile ranges for this frame ──────────
            cmb.SetBufferData(cache.tilePairCounter, s_ZeroUint, 0, 0, 1);
            // Clear tile ranges (uint2 per tile = 2 uints, init to 0)
            // Reuse a static buffer to avoid per-frame allocation
            if (_tileRangesClearBuf == null || _tileRangesClearBuf.Length < numTiles * 2)
                _tileRangesClearBuf = new uint[numTiles * 2];
            System.Array.Clear(_tileRangesClearBuf, 0, numTiles * 2);
            cmb.SetBufferData(cache.tileRanges, _tileRangesClearBuf, 0, 0, numTiles * 2);

            // ── Kernel 1: assign each Gaussian to tiles ───────────────
            cmb.SetComputeIntParam   (_tileCs, TileProps.SplatCount,    splatCount);
            cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountX,    tileX);
            cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountY,    tileY);
            cmb.SetComputeIntParam   (_tileCs, TileProps.MaxTilePairs,  GlobalOrderGroupCache.MaxTilePairs);
            cmb.SetComputeVectorParam(_tileCs, TileProps.ScreenParams,
                new Vector4(screenW, screenH, 1f / screenW, 1f / screenH));
            cmb.SetComputeBufferParam(_tileCs, _kernelTileAssign, TileProps.SplatViewData,   cache.viewData);
            cmb.SetComputeBufferParam(_tileCs, _kernelTileAssign, TileProps.TilePairCounter, cache.tilePairCounter);
            cmb.SetComputeBufferParam(_tileCs, _kernelTileAssign, TileProps.TileKeys,        cache.tileKeys);
            cmb.SetComputeBufferParam(_tileCs, _kernelTileAssign, TileProps.TileValues,      cache.tileValues);
            int tileAssignGroups = (splatCount + 1023) / 1024;
            cmb.DispatchCompute(_tileCs, _kernelTileAssign, tileAssignGroups, 1, 1);

            // ── Sort tile pairs by (tile_id | depth) ──────────────────
            // We sort 'sortCount' elements (previous frame's pair count).
            // Frame N writes pairs in slots [0, N-count), while this sorts
            // [0, (N-1)-count) — a 1-frame lag that's stable for typical scenes.
            cache.tileSorterArgs.inputKeys   = cache.tileKeys;
            cache.tileSorterArgs.inputValues = cache.tileValues;
            cache.tileSorterArgs.count       = sortCount;
            _tileSorter.Dispatch(cmb, cache.tileSorterArgs);

            // ── Kernel 2: build tile ranges from sorted keys ──────────
            cmb.SetComputeIntParam   (_tileCs, TileProps.TilePairCount,  (int)sortCount);
            cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountX,     tileX);
            cmb.SetComputeBufferParam(_tileCs, _kernelBuildRanges, TileProps.SortedTileKeys, cache.tileKeys);
            cmb.SetComputeBufferParam(_tileCs, _kernelBuildRanges, TileProps.TileRanges,     cache.tileRanges);
            int buildGroups = ((int)sortCount + 1023) / 1024;
            cmb.DispatchCompute(_tileCs, _kernelBuildRanges, buildGroups, 1, 1);

            renderOnly:
            // ── Kernel 3: tile-based alpha composite ──────────────────
            // Write directly into GaussianSplatRT (enableRandomWrite=true, set by URP feature).
            // GaussianSplatRT was cleared by CoreUtils.SetRenderTarget before this dispatch,
            // so no explicit clear needed. Tiles with no splats leave pixels as (0,0,0,0).
            cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountX,    tileX);
            cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountY,    tileY);
            cmb.SetComputeVectorParam(_tileCs, TileProps.ScreenParams,
                new Vector4(screenW, screenH, 1f / screenW, 1f / screenH));
            cmb.SetComputeBufferParam(_tileCs, _kernelRenderTiles, TileProps.SplatViewData,    cache.viewData);
            cmb.SetComputeBufferParam(_tileCs, _kernelRenderTiles, TileProps.SortedTileValues, cache.tileValues);
            cmb.SetComputeBufferParam(_tileCs, _kernelRenderTiles, TileProps.TileRangesRead,   cache.tileRanges);
            // Bind GaussianSplatRT as UAV output (passed from URP feature via TileOutputTarget)
            cmb.SetComputeTextureParam(_tileCs, _kernelRenderTiles, TileProps.OutputTexture, TileOutputTarget);
            cmb.DispatchCompute(_tileCs, _kernelRenderTiles, tileX, tileY, 1);
        }

        Material SortAndRenderSplatsGlobal(Camera cam, CommandBuffer cmb)
        {
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
                if (!EnsureGlobalGroupCache(order, reference, groupSplatCount, out var groupCache))
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
                    if (gs._frameCounter % nth == 0)
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
                    gs.EnsureMaterials();
                    matComposite = gs._matComposite;
                    groupDisplayMat ??= gs._matSplats;
                    if (gs._matSplats == null)
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
                    ++gs._frameCounter;
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

                if (groupSortNeeded || !groupCache.hasSortedKeys)
                {
                    groupCache.sorterArgs.count = (uint)dstOffset;
                    _globalSorter.Dispatch(cmb, groupCache.sorterArgs);
                    groupCache.hasSortedKeys = true;
                }
                groupCache.groupSignature = groupSignature;

                cmb.BeginSample(ProfDraw);
                // ── Tile-based rendering path ──────────────────────────
                bool usedTile = false;
                if (reference.csTileRender != null &&
                    EnsureTileResources(reference, groupCache,
                        cam.pixelWidth, cam.pixelHeight, dstOffset))
                {
                    DispatchTileRender(cmb, cam, groupCache, dstOffset,
                        groupSortNeeded || !groupCache.hasSortedKeys);
                    usedTile = true;
                }

                // ── Fallback: traditional DrawProcedural ───────────────
                if (!usedTile)
                {
                    _globalMpb.Clear();
                    _globalMpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, groupCache.viewData);
                    _globalMpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, groupCache.sortKeys);
                    cmb.DrawProcedural(reference._gpuIndexBuffer, Matrix4x4.identity,
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
            Material matComposite = null;
            foreach (var kvp in _activeSplats)
            {
                var gs = kvp.Item1;
                gs.EnsureMaterials();
                matComposite = gs._matComposite;
                var mpb = kvp.Item2;

                // sort
                var matrix = gs.transform.localToWorldMatrix;
                if (gs._frameCounter % gs.sortNthFrame == 0)
                    gs.SortPoints(cmb, cam, matrix);
                ++gs._frameCounter;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = gs.renderMode switch
                {
                    GaussianSplatRenderer.RenderMode.DebugPoints => gs._matDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugPointIndices => gs._matDebugPoints,
                    GaussianSplatRenderer.RenderMode.DebugBoxes => gs._matDebugBoxes,
                    GaussianSplatRenderer.RenderMode.DebugChunkBounds => gs._matDebugBoxes,
                    _ => gs._matSplats
                };
                if (displayMat == null)
                    continue;

                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs._gpuChunks);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs._gpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs._gpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.splatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.opacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, gs.pointDisplaySize);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.shOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.shOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, gs.renderMode == GaussianSplatRenderer.RenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, gs.renderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(ProfCalcView);
                bool calcSuccess = gs.CalcViewData(cmb, cam);
                cmb.EndSample(ProfCalcView);
                if (!calcSuccess)
                    continue;

                // draw
                int indexCount = 6;
                int instanceCount = gs.EffectiveSplatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (gs.renderMode is GaussianSplatRenderer.RenderMode.DebugBoxes or GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (gs.renderMode == GaussianSplatRenderer.RenderMode.DebugChunkBounds)
                    instanceCount = gs._gpuChunksValid ? gs._gpuChunks.count : 0;

                cmb.BeginSample(ProfDraw);
                cmb.DrawProcedural(gs._gpuIndexBuffer, matrix, displayMat, 0, topology, indexCount, instanceCount, mpb);
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

            // We only need this to determine whether we're rendering into backbuffer or not. However, detection this
            // way only works in BiRP so only do it here.
            _commandBuffer.SetGlobalTexture(GaussianSplatRenderer.Props.CameraTargetTexture, BuiltinRenderTextureType.CameraTarget);

            // add sorting, view calc and drawing commands for each splat object
            Material matComposite = SortAndRenderSplats(cam, _commandBuffer);

            // compose
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

    [ExecuteInEditMode]
    public class GaussianSplatRenderer : MonoBehaviour
    {
        public enum RenderMode
        {
            Splats,
            DebugPoints,
            DebugPointIndices,
            DebugBoxes,
            DebugChunkBounds,
        }
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

        [FormerlySerializedAs("m_RenderMode")] public RenderMode renderMode = RenderMode.Splats;
        [Range(1.0f,15.0f)] [FormerlySerializedAs("m_PointDisplaySize")] public float pointDisplaySize = 3.0f;

        [FormerlySerializedAs("m_Cutouts")] public GaussianCutout[] cutouts;

        [FormerlySerializedAs("m_ShaderSplats")] public Shader shaderSplats;
        [FormerlySerializedAs("m_ShaderComposite")] public Shader shaderComposite;
        [FormerlySerializedAs("m_ShaderDebugPoints")] public Shader shaderDebugPoints;
        [FormerlySerializedAs("m_ShaderDebugBoxes")] public Shader shaderDebugBoxes;
        [Tooltip("Gaussian splatting compute shader")]
        [FormerlySerializedAs("m_CSSplatUtilities")] public ComputeShader csSplatUtilities;
        [Tooltip("Tile-based rendering compute shader (optional; uses DrawProcedural fallback if null)")]
        public ComputeShader csTileRender;

        int _splatCount; // initially same as asset splat count, but editing can change this
        GraphicsBuffer _gpuSortDistances;
        internal GraphicsBuffer _gpuSortKeys;
        GraphicsBuffer _gpuPosData;
        GraphicsBuffer _gpuOtherData;
        GraphicsBuffer _gpuSHData;

        // Set by GaussianAnimator: per-splat animation output (3 float4s per splat)
        internal GraphicsBuffer _animOutputBuffer;
        // Set by GaussianMorph: pre-blended splat data (4 float4s per splat)
        internal GraphicsBuffer _morphedDataBuffer;
        internal GraphicsBuffer _morphSHBuffer;
        internal int _morphDataValid;
        internal int _morphedSplatCount;
        internal float _morphWeight;
        Texture _gpuColorData;
        internal GraphicsBuffer _gpuChunks;
        internal bool _gpuChunksValid;
        internal GraphicsBuffer _gpuView;
        internal GraphicsBuffer _gpuIndexBuffer;

        // these buffers are only for splat editing, and are lazily created
        GraphicsBuffer _gpuEditCutouts;
        GraphicsBuffer _gpuEditCountsBounds;
        GraphicsBuffer _gpuEditSelected;
        GraphicsBuffer _gpuEditDeleted;
        GraphicsBuffer _gpuEditSelectedMouseDown; // selection state at start of operation
        GraphicsBuffer _gpuEditPosMouseDown; // position state at start of operation
        GraphicsBuffer _gpuEditOtherMouseDown; // rotation/scale state at start of operation

        GpuSorting _sorter;
        GpuSorting.Args _sorterArgs;
        readonly Dictionary<string, int> _kernelIndexCache = new();

        internal Material _matSplats;
        internal Material _matComposite;
        internal Material _matDebugPoints;
        internal Material _matDebugBoxes;

        internal int _frameCounter;
        GaussianSplatAsset _prevAsset;
        private int _deferAssetFrames = 0;
        private const int DeferFrames = 1;
        Hash128 _prevHash;
        bool _registered;

        static readonly ProfilerMarker ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);

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

        [field: NonSerialized] public bool editModified { get; private set; }
        [field: NonSerialized] public uint editSelectedSplats { get; private set; }
        [field: NonSerialized] public uint editDeletedSplats { get; private set; }
        [field: NonSerialized] public uint editCutSplats { get; private set; }
        [field: NonSerialized] public Bounds editSelectedBounds { get; private set; }

        public GaussianSplatAsset asset => splatAsset;
        public int splatCount => _splatCount;

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

        internal void SetMorphData(GraphicsBuffer morphedData, int splatCount, int valid, float weight = 0f, GraphicsBuffer morphSH = null)
        {
            _morphedDataBuffer = morphedData;
            _morphSHBuffer = morphSH;
            _morphedSplatCount = splatCount;
            _morphDataValid = valid;
            _morphWeight = weight;

            // Always check buffer capacity — CreateResourcesForAsset may have
            // rebuilt sort buffers at source count since our last call.
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

        enum KernelIndices
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

        static readonly string[] KernelNames =
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
            // cube indices, most often we use only the first quad
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
                // just a dummy chunk buffer
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>())
                { name = "GaussianChunkData" };
            }
            _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatAsset.splatCount, GpuViewDataSize);
            _gpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
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
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
            _gpuColorData = tex;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
            {
                _gpuChunks.SetData(asset.chunkData.GetData<GaussianSplatAsset.ChunkInfo>());
                _gpuChunksValid = true;
            }
            else
            {
                // just a dummy chunk buffer
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
            var tex = new Texture2D(texWidth, texHeight, texFormat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.IgnoreMipmapLimit | TextureCreationFlags.DontUploadUponCreate) { name = "GaussianColorData" };
            tex.SetPixelData(asset.colorData.GetData<byte>(), 0);
            tex.Apply(false, true);
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
                // just a dummy chunk buffer
                _gpuChunks = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1,
                    UnsafeUtility.SizeOf<GaussianSplatAsset.ChunkInfo>()) {name = "GaussianChunkData"};
                _gpuChunksValid = false;
            }

            _gpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatAsset.splatCount, GpuViewDataSize);
            _gpuIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, 36, 2);
            // cube indices, most often we use only the first quad
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

        private void InitSortBuffers(int count)
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
                // Fallback when CSSetIndices is not available/supported.
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

        private bool TryFindSupportedKernel(string kernelName, out int kernelIndex)
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

        private bool TryFindSupportedKernel(KernelIndices kernel, out int kernelIndex)
        {
            kernelIndex = -1;
            int idx = (int)kernel;
            if (idx < 0 || idx >= KernelNames.Length)
                return false;
            return TryFindSupportedKernel(KernelNames[idx], out kernelIndex);
        }

        bool resourcesAreSetUp => shaderSplats != null && shaderComposite != null && shaderDebugPoints != null &&
                                  shaderDebugBoxes != null && csSplatUtilities != null && SystemInfo.supportsComputeShaders;

        public void EnsureMaterials()
        {
            if (_matSplats == null && resourcesAreSetUp)
            {
                _matSplats = new Material(shaderSplats) {name = "GaussianSplats"};
                _matComposite = new Material(shaderComposite) {name = "GaussianClearDstAlpha"};
                _matDebugPoints = new Material(shaderDebugPoints) {name = "GaussianDebugPoints"};
                _matDebugBoxes = new Material(shaderDebugBoxes) {name = "GaussianDebugBoxes"};
            }
            // Auto-load tile render compute shader if not assigned in Inspector
            if (csTileRender == null)
                csTileRender = Resources.Load<ComputeShader>("GaussianTileRender");
        }

        public void EnsureSorterAndRegister()
        {
            if (_sorter == null && resourcesAreSetUp)
            {
                _sorter = new GpuSorting(csSplatUtilities);
            }

            if (!_registered && resourcesAreSetUp)
            {
                GaussianSplatRenderSystem.instance.RegisterSplat(this);
                _registered = true;
            }
        }

        private void OnEnable()
        {
            _frameCounter = 0;
            if (!resourcesAreSetUp)
                return;

            EnsureMaterials();
            EnsureSorterAndRegister();

            CreateResourcesForAsset();
        }

        private bool SetAssetDataOnCS(CommandBuffer cmb, KernelIndices kernel, out int kernelIndex)
        {
            if (!TryFindSupportedKernel(kernel, out kernelIndex))
                return false;
            SetAssetDataOnCS(cmb, kernelIndex);
            return true;
        }

        private void SetAssetDataOnCS(CommandBuffer cmb, int kernelIndex)
        {
            ComputeShader cs = csSplatUtilities;
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatPos, _gpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatChunks, _gpuChunks);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatOther, _gpuOtherData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSH, _gpuSHData);
            cmb.SetComputeTextureParam(cs, kernelIndex, Props.SplatColor, _gpuColorData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatSelectedBits, _gpuEditSelected ?? _gpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatDeletedBits, _gpuEditDeleted ?? _gpuPosData);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatViewData, _gpuView);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.OrderBuffer, _gpuSortKeys);

            cmb.SetComputeIntParam(cs, Props.SplatBitsValid, _gpuEditSelected != null && _gpuEditDeleted != null ? 1 : 0);
            uint format = (uint)splatAsset.posFormat | ((uint)splatAsset.scaleFormat << 8) | ((uint)splatAsset.shFormat << 16);
            cmb.SetComputeIntParam(cs, Props.SplatFormat, (int)format);
            cmb.SetComputeIntParam(cs, Props.SplatCount, EffectiveSplatCount);
            cmb.SetComputeIntParam(cs, Props.SrcSplatCount, _splatCount);
            cmb.SetComputeIntParam(cs, Props.SplatChunkCount, _gpuChunksValid ? _gpuChunks.count : 0);

            UpdateCutoutsBuffer();
            cmb.SetComputeIntParam(cs, Props.SplatCutoutsCount, cutouts?.Length ?? 0);
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.SplatCutouts, _gpuEditCutouts);

            // Animation data binding
            bool hasAnim = _animOutputBuffer != null;
            cmb.SetComputeIntParam(cs, Props.AnimDataValid, hasAnim ? 1 : 0);
            // Always bind a valid buffer to avoid shader errors
            cmb.SetComputeBufferParam(cs, kernelIndex, Props.AnimOutputData, hasAnim ? _animOutputBuffer : _gpuView);

            // Morph data binding
            // _MorphDataValid: 0 = no morph, 1 = morph (no SH), 2 = morph with blended SH
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
            mat.SetBuffer(Props.SplatSelectedBits, _gpuEditSelected ?? _gpuPosData);
            mat.SetBuffer(Props.SplatDeletedBits, _gpuEditDeleted ?? _gpuPosData);
            mat.SetInt(Props.SplatBitsValid, _gpuEditSelected != null && _gpuEditDeleted != null ? 1 : 0);
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

            DisposeBuffer(ref _gpuEditSelectedMouseDown);
            DisposeBuffer(ref _gpuEditPosMouseDown);
            DisposeBuffer(ref _gpuEditOtherMouseDown);
            DisposeBuffer(ref _gpuEditSelected);
            DisposeBuffer(ref _gpuEditDeleted);
            DisposeBuffer(ref _gpuEditCountsBounds);
            DisposeBuffer(ref _gpuEditCutouts);

            _sorterArgs.resources.Dispose();

            _splatCount = 0;
            _gpuChunksValid = false;

            editSelectedSplats = 0;
            editDeletedSplats = 0;
            editCutSplats = 0;
            editModified = false;
            editSelectedBounds = default;
        }

        private void OnDisable()
        {
            DisposeResourcesForAsset();
            _kernelIndexCache.Clear();
            GaussianSplatRenderSystem.instance.UnregisterSplat(this);
            _registered = false;

            DestroyImmediate(_matSplats);
            DestroyImmediate(_matComposite);
            DestroyImmediate(_matDebugPoints);
            DestroyImmediate(_matDebugBoxes);
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

            // calculate view dependent data for each splat
            if (!SetAssetDataOnCS(cmb, KernelIndices.CalcViewData, out int kernelIndex))
                return false;

            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, matView * matO2W);
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
                   _matSplats != null &&
                   _matComposite != null &&
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

            // calculate distance to the camera for each splat
            cmd.BeginSample(ProfSort);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatSortDistances, _gpuSortDistances);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatSortKeys, _gpuSortKeys);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatChunks, _gpuChunks);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatPos, _gpuPosData);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatFormat, (int)splatAsset.posFormat);
            cmd.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, worldToCamMatrix * matrix);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatCount, EffectiveSplatCount);
            cmd.SetComputeIntParam(csSplatUtilities, Props.SplatChunkCount, _gpuChunksValid ? _gpuChunks.count : 0);

            // Bind morph data so CalcDistances can use morphed positions
            bool hasMorph = _morphedDataBuffer != null && _morphDataValid != 0;
            cmd.SetComputeIntParam(csSplatUtilities, Props.MorphDataValid, hasMorph ? 1 : 0);
            cmd.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.MorphedData, hasMorph ? _morphedDataBuffer : _gpuSortDistances);

            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            cmd.DispatchCompute(csSplatUtilities, kernelIndex, (EffectiveSplatCount + (int)gsX - 1)/(int)gsX, 1, 1);

            // sort the splats — use effective count, not buffer size,
            // to avoid sorting stale entries from pre-allocated buffers
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

            // Delay applying splatAsset by deferred frames.
            if (_deferAssetFrames > 0)
            {
                _deferAssetFrames--;
                if (_deferAssetFrames == 0 && nextAsset != null)
                {
                    splatAsset = nextAsset;
                    _prevAsset = null; // force asset resources refresh
                    _prevHash = default;
                }
            }

            var curHash = splatAsset ? splatAsset.dataHash : new Hash128();
            if (_prevAsset != splatAsset || _prevHash != curHash)
            {
                _prevAsset = splatAsset;
                _prevHash = curHash;
                if (resourcesAreSetUp)
                {
                    CreateResourcesForAsset();
                }
                else
                {
                    Debug.LogError($"{nameof(GaussianSplatRenderer)} component is not set up correctly (Resource references are missing), or platform does not support compute shaders");
                }
            }
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

        private void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            if (!TryFindSupportedKernel(KernelIndices.ClearBuffer, out int kernelIndex))
                return;
            csSplatUtilities.SetBuffer(kernelIndex, Props.DstBuffer, buf);
            csSplatUtilities.SetInt(Props.BufferSize, buf.count);
            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            csSplatUtilities.Dispatch(kernelIndex, (int)((buf.count+gsX-1)/gsX), 1, 1);
        }

        private void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            if (!TryFindSupportedKernel(KernelIndices.OrBuffers, out int kernelIndex))
                return;
            csSplatUtilities.SetBuffer(kernelIndex, Props.SrcBuffer, src);
            csSplatUtilities.SetBuffer(kernelIndex, Props.DstBuffer, dst);
            csSplatUtilities.SetInt(Props.BufferSize, dst.count);
            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            csSplatUtilities.Dispatch(kernelIndex, (int)((dst.count+gsX-1)/gsX), 1, 1);
        }

        static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        public void UpdateEditCountsAndBounds()
        {
            if (_gpuEditSelected == null)
            {
                editSelectedSplats = 0;
                editDeletedSplats = 0;
                editCutSplats = 0;
                editModified = false;
                editSelectedBounds = default;
                return;
            }

            if (!TryFindSupportedKernel(KernelIndices.InitEditData, out int initKernel))
                return;
            csSplatUtilities.SetBuffer(initKernel, Props.DstBuffer, _gpuEditCountsBounds);
            csSplatUtilities.Dispatch(initKernel, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            if (!SetAssetDataOnCS(cmb, KernelIndices.UpdateEditData, out int updateKernel))
                return;
            cmb.SetComputeBufferParam(csSplatUtilities, updateKernel, Props.DstBuffer, _gpuEditCountsBounds);
            cmb.SetComputeIntParam(csSplatUtilities, Props.BufferSize, _gpuEditSelected.count);
            csSplatUtilities.GetKernelThreadGroupSizes(updateKernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(csSplatUtilities, updateKernel, (int)((_gpuEditSelected.count+gsX-1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[_gpuEditCountsBounds.count];
            _gpuEditCountsBounds.GetData(res);
            editSelectedSplats = res[0];
            editDeletedSplats = res[1];
            editCutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f,0.1f,0.1f);
            editSelectedBounds = bounds;
        }

        private void UpdateCutoutsBuffer()
        {
            int bufferSize = cutouts?.Length ?? 0;
            if (bufferSize == 0)
                bufferSize = 1;
            if (_gpuEditCutouts == null || _gpuEditCutouts.count != bufferSize)
            {
                _gpuEditCutouts?.Dispose();
                _gpuEditCutouts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, bufferSize, UnsafeUtility.SizeOf<GaussianCutout.ShaderData>()) { name = "GaussianCutouts" };
            }

            NativeArray<GaussianCutout.ShaderData> data = new(bufferSize, Allocator.Temp);
            if (cutouts != null)
            {
                var matrix = transform.localToWorldMatrix;
                for (var i = 0; i < cutouts.Length; ++i)
                {
                    data[i] = GaussianCutout.GetShaderData(cutouts[i], matrix);
                }
            }

            _gpuEditCutouts.SetData(data);
            data.Dispose();
        }

        private bool EnsureEditingBuffers()
        {
            if (!HasValidAsset || !HasValidRenderSetup)
                return false;

            if (_gpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (_splatCount + 31) / 32;
                _gpuEditSelected = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelected"};
                _gpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatSelectedInit"};
                _gpuEditDeleted = new GraphicsBuffer(target, size, 4) {name = "GaussianSplatDeleted"};
                _gpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) {name = "GaussianSplatEditData"}; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(_gpuEditSelected);
                ClearGraphicsBuffer(_gpuEditSelectedMouseDown);
                ClearGraphicsBuffer(_gpuEditDeleted);
            }
            return _gpuEditSelected != null;
        }

        public void EditStoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(_gpuEditSelected, _gpuEditSelectedMouseDown);
        }

        public void EditStorePosMouseDown()
        {
            if (_gpuEditPosMouseDown == null)
            {
                _gpuEditPosMouseDown = new GraphicsBuffer(_gpuPosData.target | GraphicsBuffer.Target.CopyDestination, _gpuPosData.count, _gpuPosData.stride) {name = "GaussianSplatEditPosMouseDown"};
            }
            Graphics.CopyBuffer(_gpuPosData, _gpuEditPosMouseDown);
        }
        public void EditStoreOtherMouseDown()
        {
            if (_gpuEditOtherMouseDown == null)
            {
                _gpuEditOtherMouseDown = new GraphicsBuffer(_gpuOtherData.target | GraphicsBuffer.Target.CopyDestination, _gpuOtherData.count, _gpuOtherData.stride) {name = "GaussianSplatEditOtherMouseDown"};
            }
            Graphics.CopyBuffer(_gpuOtherData, _gpuEditOtherMouseDown);
        }

        public void EditUpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(_gpuEditSelectedMouseDown, _gpuEditSelected);

            var tr = transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            if (!SetAssetDataOnCS(cmb, KernelIndices.SelectionUpdate, out _))
                return;

            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(csSplatUtilities, Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(csSplatUtilities, Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(csSplatUtilities, Props.SelectionRect, new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(csSplatUtilities, Props.SelectionMode, subtract ? 0 : 1);

            if (!DispatchUtilsAndExecute(cmb, KernelIndices.SelectionUpdate, _splatCount))
                return;
            UpdateEditCountsAndBounds();
        }

        public void EditTranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            if (!SetAssetDataOnCS(cmb, KernelIndices.TranslateSelection, out _))
                return;

            cmb.SetComputeVectorParam(csSplatUtilities, Props.SelectionDelta, localSpacePosDelta);

            if (!DispatchUtilsAndExecute(cmb, KernelIndices.TranslateSelection, _splatCount))
                return;
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditRotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (_gpuEditPosMouseDown == null || _gpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            if (!SetAssetDataOnCS(cmb, KernelIndices.RotateSelection, out int kernelIndex))
                return;

            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatPosMouseDown, _gpuEditPosMouseDown);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatOtherMouseDown, _gpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(csSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(csSplatUtilities, Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            if (!DispatchUtilsAndExecute(cmb, KernelIndices.RotateSelection, _splatCount))
                return;
            UpdateEditCountsAndBounds();
            editModified = true;
        }


        public void EditScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (_gpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            if (!SetAssetDataOnCS(cmb, KernelIndices.ScaleSelection, out int kernelIndex))
                return;

            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.SplatPosMouseDown, _gpuEditPosMouseDown);
            cmb.SetComputeVectorParam(csSplatUtilities, Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(csSplatUtilities, Props.SelectionDelta, scale);

            if (!DispatchUtilsAndExecute(cmb, KernelIndices.ScaleSelection, _splatCount))
                return;
            UpdateEditCountsAndBounds();
            editModified = true;
        }

        public void EditDeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(_gpuEditDeleted, _gpuEditSelected);
            EditDeselectAll();
            UpdateEditCountsAndBounds();
            if (editDeletedSplats != 0)
                editModified = true;
        }

        public void EditSelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            if (!SetAssetDataOnCS(cmb, KernelIndices.SelectAll, out int kernelIndex))
                return;
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.DstBuffer, _gpuEditSelected);
            cmb.SetComputeIntParam(csSplatUtilities, Props.BufferSize, _gpuEditSelected.count);
            if (!DispatchUtilsAndExecute(cmb, KernelIndices.SelectAll, _gpuEditSelected.count))
                return;
            UpdateEditCountsAndBounds();
        }

        public void EditDeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(_gpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void EditInvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            if (!SetAssetDataOnCS(cmb, KernelIndices.InvertSelection, out int kernelIndex))
                return;
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, Props.DstBuffer, _gpuEditSelected);
            cmb.SetComputeIntParam(csSplatUtilities, Props.BufferSize, _gpuEditSelected.count);
            if (!DispatchUtilsAndExecute(cmb, KernelIndices.InvertSelection, _gpuEditSelected.count))
                return;
            UpdateEditCountsAndBounds();
        }

        public bool EditExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            if (!SetAssetDataOnCS(cmb, KernelIndices.ExportData, out int kernelIndex))
                return false;
            cmb.SetComputeIntParam(csSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(csSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(csSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(csSplatUtilities, Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, "_ExportBuffer", dstData);

            if (!DispatchUtilsAndExecute(cmb, KernelIndices.ExportData, _splatCount))
                return false;
            return true;
        }

        public void EditSetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.MaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == splatCount)
                return;

            int posStride = (int)(asset.posData.dataSize / asset.splatCount);
            int otherStride = (int)(asset.otherData.dataSize / asset.splatCount);
            int shStride = (int) (asset.shData.dataSize / asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelected"};
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatSelectedInit"};
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) {name = "GaussianSplatDeleted"};
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount, GpuViewDataSize);
            InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            EditCopySplats(transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, _splatCount);

            // use the new buffers and the new splat count
            _gpuPosData.Dispose();
            _gpuOtherData.Dispose();
            _gpuSHData.Dispose();
            DestroyImmediate(_gpuColorData);
            _gpuView.Dispose();

            _gpuEditSelected?.Dispose();
            _gpuEditSelectedMouseDown?.Dispose();
            _gpuEditDeleted?.Dispose();

            _gpuPosData = newPosData;
            _gpuOtherData = newOtherData;
            _gpuSHData = newSHData;
            _gpuColorData = newColorData;
            _gpuView = newGpuView;
            _gpuEditSelected = newEditSelected;
            _gpuEditSelectedMouseDown = newEditSelectedMouseDown;
            _gpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref _gpuEditPosMouseDown);
            DisposeBuffer(ref _gpuEditOtherMouseDown);

            _splatCount = newSplatCount;
            editModified = true;
        }

        public void EditCopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            EditCopySplats(
                dst.transform,
                dst._gpuPosData, dst._gpuOtherData, dst._gpuSHData, dst._gpuColorData, dst._gpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.editModified = true;
        }

        public void EditCopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            if (!SetAssetDataOnCS(cmb, KernelIndices.CopySplats, out int kernelIndex))
                return;

            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(csSplatUtilities, kernelIndex, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(csSplatUtilities, kernelIndex, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(csSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(csSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(csSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(csSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(csSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(csSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(csSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, KernelIndices.CopySplats, copyCount);
        }

        private bool DispatchUtilsAndExecute(CommandBuffer cmb, KernelIndices kernel, int count)
        {
            if (count <= 0)
            {
                Graphics.ExecuteCommandBuffer(cmb);
                return true;
            }
            if (!TryFindSupportedKernel(kernel, out int kernelIndex))
                return false;
            csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            cmb.DispatchCompute(csSplatUtilities, kernelIndex, (int)((count + gsX - 1)/gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
            return true;
        }

        public GraphicsBuffer GpuEditDeleted => _gpuEditDeleted;
    }
}
