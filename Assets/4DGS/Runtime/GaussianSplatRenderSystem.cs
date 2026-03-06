// SPDX-License-Identifier: MIT

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

        // ── Cross-camera GPU synchronisation ─────────────────────────
        // On D3D12/Vulkan, render graph does not insert UAV barriers for
        // manually-managed GraphicsBuffers between different cameras' passes.
        // A GraphicsFence at the end of each camera's splat work ensures the
        // next camera waits for completion before reusing shared buffers.
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
            // Per-camera async readback of tile pair counts.
            // Each camera gets its own entry so that readback results from one
            // camera never overwrite the sort budget of another.
            public readonly Dictionary<int, uint> asyncTilePairCounts = new();
            public readonly HashSet<int> asyncReadbackPending = new(); // camera IDs with in-flight readbacks
            // Buffers retired during grow — kept alive until next frame so in-flight
            // CommandBuffers from other cameras don't reference freed memory.
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

        public static int MaxTilePairsCapacity => GlobalOrderGroupCache.MaxTilePairs;

        // Latest tile pair count from async readback (for diagnostics / Inspector).
        public uint LastTilePairCount => _tileRenderer?.GetLastTilePairCount(_globalGroups) ?? 0;

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            // Wait for the previous camera's GPU work on shared buffers to
            // complete.  Required on D3D12/Vulkan where the render graph does
            // not insert UAV barriers for our manually-managed GraphicsBuffers.
            // On Metal/OpenGL this is a no-op (already signaled).
            if (_hasRenderFence)
                cmb.WaitOnAsyncGraphicsFence(_lastRenderFence);

            Material result = CanUseGlobalSortPath()
                ? SortAndRenderSplatsGlobal(cam, cmb)
                : SortAndRenderSplatsPerObject(cam, cmb);

            // Fence after all compute/draw commands so the next camera can
            // synchronise on our completion.
            _lastRenderFence = cmb.CreateAsyncGraphicsFence();
            _hasRenderFence = true;

            return result;
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

        Material SortAndRenderSplatsGlobal(Camera cam, CommandBuffer cmb)
        {
            // Dispose retired tile-range buffers from previous frames.
            // Safe here: previous frame's GPU work is complete by the time a new
            // Unity frame starts.  Only run once per Unity frame.
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

                groupCache.groupSignature = groupSignature;

                cmb.BeginSample(ProfDraw);
                // ── Tile-based rendering path ──────────────────────────
                bool usedTile = false;
                // Use cam.pixelWidth/Height for the tile grid — this matches the
                // coordinate space used by CalcViewData's focal-length computation.
                // TileRenderSize is kept for future render-scale support.
                int tileW = cam.pixelWidth;
                int tileH = cam.pixelHeight;
                _tileRenderer ??= new GaussianTileRenderer();
                if (reference.useTileRenderer && reference.csTileRender != null &&
                    _tileRenderer.EnsureResources(reference, groupCache, tileW, tileH))
                {
                    _tileRenderer.Dispatch(cmb, cam, groupCache, dstOffset, groupSortNeeded, tileW, tileH);
                    usedTile = true;
                }

                // Global distance sort is only needed for the DrawProcedural
                // fallback path; tile path does its own per-tile sort.
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
}
