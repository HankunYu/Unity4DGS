using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.XR;

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

        // ── Deferred buffer disposal ─────────────────────────────────────
        // With a threaded graphics device the render thread may not have
        // translated this frame's recorded draws when the main thread disposes
        // a buffer they bind — Vulkan then drops those draws ("none provided").
        // Teardown/recreate paths park buffers here instead; they are disposed
        // on a later frame, with assembly-reload/quit as the backstop.
        static readonly List<(GraphicsBuffer buf, int frame)> s_deferredDisposal = new();

        static GaussianSplatRenderSystem()
        {
#if UNITY_EDITOR
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += () => FlushDeferredDisposal(true);
            // Vulkan binding errors are native render-thread logs with no C#
            // stack — flag them here, dump the draw journal on the main thread.
            Application.logMessageReceivedThreaded += (msg, _, _) =>
            {
                if (msg.StartsWith("Vulkan: Shader requires a compute buffer"))
                    s_bindingErrorSeen = true;
            };
#endif
            Application.quitting += () => FlushDeferredDisposal(true);
        }

        // ── Draw/dispose journal for correlating native binding errors ──
        static readonly Queue<string> s_journal = new();
        static volatile bool s_bindingErrorSeen;

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        internal static void Journal(string entry)
        {
            s_journal.Enqueue($"f{Time.frameCount} {entry}");
            while (s_journal.Count > 96)
                s_journal.Dequeue();
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        static void DumpJournalOnBindingError()
        {
            if (!s_bindingErrorSeen)
                return;
            s_bindingErrorSeen = false;
            Debug.LogWarning($"[GaussianSplat] Vulkan binding error detected (now f{Time.frameCount}); recent journal:\n" +
                             string.Join("\n", s_journal));
            s_journal.Clear();
        }

        internal static void DeferDispose(GraphicsBuffer buf)
        {
            if (buf == null)
                return;
            Journal($"defer-dispose #{buf.GetHashCode():X8} count={buf.count} stride={buf.stride}");
            s_deferredDisposal.Add((buf, Time.frameCount));
        }

        internal static void FlushDeferredDisposal(bool force = false)
        {
            for (int i = s_deferredDisposal.Count - 1; i >= 0; --i)
            {
                if (force || Time.frameCount > s_deferredDisposal[i].frame)
                {
                    Journal($"dispose #{s_deferredDisposal[i].buf.GetHashCode():X8}");
                    s_deferredDisposal[i].buf.Dispose();
                    s_deferredDisposal.RemoveAt(i);
                }
            }
        }

        readonly Dictionary<GaussianSplatRenderer, MaterialPropertyBlock> _splats = new();
        readonly HashSet<Camera> _cameraCommandBuffersDone = new();
        readonly List<(GaussianSplatRenderer, MaterialPropertyBlock)> _activeSplats = new();
        readonly MaterialPropertyBlock _globalMpb = new();
        readonly Dictionary<int, GlobalOrderGroupCache> _globalGroups = new();

        CommandBuffer _commandBuffer;
        GpuSorting _globalSorter;
        ComputeShader _globalSorterShader;
        GpuCountingSort _globalCountingSorter;
        ComputeShader _globalCountingSortShader;
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
        private Material _matPointCloud;
        private GaussianSplatConfig _materialSource;

        internal Material MatSplats => _matSplats;
        internal Material MatComposite => _matComposite;
        internal Material MatDebugPoints => _matDebugPoints;
        internal Material MatDebugBoxes => _matDebugBoxes;
        internal Material MatPointCloud => _matPointCloud;

        internal void EnsureMaterials()
        {
            if (_config == null) return;
            if (_matSplats != null && _materialSource == _config) return;

            Object.DestroyImmediate(_matSplats);
            Object.DestroyImmediate(_matComposite);
            Object.DestroyImmediate(_matDebugPoints);
            Object.DestroyImmediate(_matDebugBoxes);
            Object.DestroyImmediate(_matPointCloud);

            _matSplats = new Material(_config.ShaderSplats) { name = "GaussianSplats" };
            _matComposite = new Material(_config.ShaderComposite) { name = "GaussianClearDstAlpha" };
            _matDebugPoints = new Material(_config.ShaderDebugPoints) { name = "GaussianDebugPoints" };
            _matDebugBoxes = new Material(_config.ShaderDebugBoxes) { name = "GaussianDebugBoxes" };
            _matPointCloud = new Material(_config.ShaderPointCloud) { name = "GaussianPointCloud" };

            _materialSource = _config;
        }

        // ── Cross-camera GPU synchronisation ─────────────────────────
        GraphicsFence _lastRenderFence;
        bool _hasRenderFence;

        // ── Stereo prepare/render split ─────────────────────────────────
        public struct StereoRenderItem
        {
            public GaussianSplatRenderer gs;
            public Material displayMat;
            public MaterialPropertyBlock mpb;
            public int indexCount;
            public int instanceCount;
            public MeshTopology topology;
            public GraphicsBuffer indirectArgs;   // non-null: indexed indirect draw
        }

        private readonly List<StereoRenderItem> _preparedItems = new();
        public int PreparedItemCount => _preparedItems.Count;

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

        // Draw-time guard: a splat draw with a null/disposed view or order
        // buffer would be rejected by the graphics backend anyway (Vulkan logs
        // a per-draw error) — skip it cleanly and record where it came from.
        private static bool BuffersValidForDraw(string site, GraphicsBuffer view, GraphicsBuffer order)
        {
            bool viewOk = view != null && view.IsValid();
            bool orderOk = order != null && order.IsValid();
            if (viewOk && orderOk)
                return true;
            Debug.LogWarning($"[GaussianSplat] Skipped {site} draw (frame {Time.frameCount}): " +
                             $"view={(view == null ? "null" : viewOk ? "ok" : "disposed")}, " +
                             $"order={(order == null ? "null" : orderOk ? "ok" : "disposed")}");
            return false;
        }

        private static readonly uint[] s_zeroCounter = { 0 };

        // Writes DrawProceduralIndirect args from the visible-splat counter so
        // the draw covers only the sorted visible front portion of the keys.
        private bool DispatchWriteIndirectArgs(CommandBuffer cmb, GaussianSplatRenderer reference,
            GraphicsBuffer counter, GraphicsBuffer args)
        {
            var cs = _config != null ? _config.CsSplatUtilities : null;
            if (cs == null || counter == null || args == null ||
                !reference.TryFindSupportedKernel(GaussianSplatRenderer.KernelIndices.WriteIndirectArgs, out int kernel))
                return false;
            cmb.SetComputeBufferParam(cs, kernel, GaussianSplatRenderer.Props.VisibleCounter, counter);
            cmb.SetComputeBufferParam(cs, kernel, GaussianSplatRenderer.Props.IndirectArgs, args);
            cmb.SetComputeIntParam(cs, GaussianSplatRenderer.Props.IndirectIndexCount, 6);
            cmb.DispatchCompute(cs, kernel, 1, 1, 1);
            return true;
        }

        internal class GlobalOrderGroupCache
        {
            public int splatCapacity;
            public int groupSignature;
            public bool hasSortedKeys;
            public int lastSortCameraId;    // instance ID of last camera that sorted the keys
            public int lastSortFrame;       // Time.frameCount of that sort
            public int lastUsedFrame;
            public GraphicsBuffer viewData;
            public GraphicsBuffer sortDistances;
            public GraphicsBuffer sortKeys;
            public GraphicsBuffer visibleCounter;   // [0] = visible splats this sort
            public GraphicsBuffer indirectArgs;     // 5 uints for DrawProceduralIndirect
            public bool indirectArgsValid;          // args written for the current keys
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
            // Buffers replaced mid-frame. Draws recorded earlier this frame (by
            // another camera) may still reference them on the render thread, so
            // actual disposal is deferred to the next Unity frame.
            public readonly List<GraphicsBuffer> retiredBuffers = new();

            public void RetireBuffer(ref GraphicsBuffer buf)
            {
                if (buf != null)
                    retiredBuffers.Add(buf);
                buf = null;
            }

            public void DisposeRetiredBuffers()
            {
                foreach (var buf in retiredBuffers)
                    buf.Dispose();
                retiredBuffers.Clear();
            }

            public void Dispose()
            {
                Journal("cache dispose");
                // Defer everything a recorded draw might still bind — this can
                // run mid-frame (last renderer unregistering on teardown).
                DeferDispose(viewData);      viewData = null;
                DeferDispose(sortDistances); sortDistances = null;
                DeferDispose(sortKeys);      sortKeys = null;
                DeferDispose(visibleCounter); visibleCounter = null;
                DeferDispose(indirectArgs);  indirectArgs = null;
                indirectArgsValid = false;
                sorterArgs.resources.Dispose();

                DeferDispose(tileKeys);      tileKeys = null;
                DeferDispose(tileValues);    tileValues = null;
                DeferDispose(tileRanges);    tileRanges = null;
                tileRangesCapacity = 0;
                foreach (var buf in retiredBuffers)
                    DeferDispose(buf);
                retiredBuffers.Clear();
                DeferDispose(tilePairCounter); tilePairCounter = null;
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
            _globalCountingSorter?.Dispose();
            _globalCountingSorter = null;
            _globalCountingSortShader = null;
            _tileRenderer = null;
            _hasRenderFence = false;

            Object.DestroyImmediate(_matSplats);
            Object.DestroyImmediate(_matComposite);
            Object.DestroyImmediate(_matDebugPoints);
            Object.DestroyImmediate(_matDebugBoxes);
            Object.DestroyImmediate(_matPointCloud);
            _matSplats = null;
            _matComposite = null;
            _matDebugPoints = null;
            _matDebugBoxes = null;
            _matPointCloud = null;
            _materialSource = null;
        }

        public void RegisterSplat(GaussianSplatRenderer r)
        {
            Journal($"register {r.name}");
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
            Journal($"unregister {r.name}");
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
            FlushDeferredDisposal();
            DumpJournalOnBindingError();

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

        // Stereo path: prepare (sort + CalcViewData) once, then render per eye.
        // Only supports per-object path (no global sort) for now.
        public Material PrepareSplats(Camera cam, CommandBuffer cmb)
        {
            // Skip async fence on XR devices — visionOS Metal may stall on
            // cross-frame AsyncGraphicsFence, blocking all subsequent GPU work.
            bool useFence = Application.isEditor || !XRSettings.enabled;

            if (_hasRenderFence && useFence)
                cmb.WaitOnAsyncGraphicsFence(_lastRenderFence);

            _preparedItems.Clear();
            Material matComposite = _matComposite;

            foreach (var kvp in _activeSplats)
            {
                var gs = kvp.Item1;
                var mpb = kvp.Item2;

                var matrix = gs.transform.localToWorldMatrix;

                mpb.Clear();
                Material displayMat = _config.renderMode switch
                {
                    GaussianSplatRenderMode.DebugPoints => _matDebugPoints,
                    GaussianSplatRenderMode.DebugPointIndices => _matDebugPoints,
                    GaussianSplatRenderMode.DebugBoxes => _matDebugBoxes,
                    GaussianSplatRenderMode.DebugChunkBounds => _matDebugBoxes,
                    GaussianSplatRenderMode.PointCloud => _matPointCloud,
                    _ => _matSplats
                };
                if (displayMat == null)
                    continue;

                if (!BuffersValidForDraw($"prepare:{gs.name}", gs.GpuView, gs.GpuSortKeys))
                    continue;

                Journal($"prepare {gs.name} cam={cam.name}");
                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.GpuChunksBuffer);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.GpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.splatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.opacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, _config.pointDisplaySize);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointSizeScale, _config.pointCloudSizeScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointMinSize, _config.pointCloudMinDisplaySize);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointMinWorldSize, _config.pointCloudMinWorldSize);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointProjectionScale, cam.projectionMatrix.m11);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointMaxSize, _config.pointCloudMaxDisplaySize);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointOpacityBoost, _config.pointCloudOpacityBoost);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.shOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.shOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex,
                    _config.renderMode == GaussianSplatRenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks,
                    _config.renderMode == GaussianSplatRenderMode.DebugChunkBounds ? 1 : 0);

                cmb.BeginSample(ProfCalcView);
                bool calcSuccess = gs.CalcViewData(cmb, cam);
                cmb.EndSample(ProfCalcView);
                if (!calcSuccess)
                    continue;

                // Sort after view data so distance keys can read visibility
                // (invisible splats sink to the tail; indirect draw skips them).
                if (gs.NeedsSort(cam))
                {
                    gs.SortPoints(cmb, cam, matrix);
                    gs.MarkSorted(cam);
                }

                int indexCount = 6;
                int instanceCount = gs.EffectiveSplatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (_config.renderMode is GaussianSplatRenderMode.DebugBoxes or GaussianSplatRenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (_config.renderMode == GaussianSplatRenderMode.DebugChunkBounds)
                    instanceCount = gs.GpuChunksValid ? gs.GpuChunksBuffer.count : 0;

                _preparedItems.Add(new StereoRenderItem
                {
                    gs = gs, displayMat = displayMat, mpb = mpb,
                    indexCount = indexCount, instanceCount = instanceCount, topology = topology,
                    indirectArgs = _config.renderMode == GaussianSplatRenderMode.Splats && gs.IndirectDrawReady
                        ? gs.GpuIndirectArgs : null
                });
            }

            if (useFence)
            {
                _lastRenderFence = cmb.CreateAsyncGraphicsFence();
                _hasRenderFence = true;
            }
            return matComposite;
        }

        // Draw all prepared items for a specific eye (0 = left, 1 = right).
        public void RenderPreparedSplats(CommandBuffer cmb, int eyeIndex)
        {
            foreach (var item in _preparedItems)
            {
                item.mpb.SetInteger(GaussianSplatRenderer.Props.EyeIndex, eyeIndex);
                item.mpb.SetInteger(GaussianSplatRenderer.Props.IsStereo, eyeIndex >= 0 ? 1 : 0);

                cmb.BeginSample(ProfDraw);
                if (item.indirectArgs != null)
                    cmb.DrawProceduralIndirect(item.gs.GpuIndexBuffer, item.gs.transform.localToWorldMatrix,
                        item.displayMat, 0, item.topology, item.indirectArgs, 0, item.mpb);
                else
                    cmb.DrawProcedural(item.gs.GpuIndexBuffer, item.gs.transform.localToWorldMatrix,
                        item.displayMat, 0, item.topology, item.indexCount, item.instanceCount, item.mpb);
                cmb.EndSample(ProfDraw);
            }
        }

        private bool CanUseGlobalSortPath()
        {
            // Global sort copies view data linearly; incompatible with stereo doubled layout
            if (XRSettings.enabled && !Application.isEditor)
                return false;

            if (_config.renderMode is not (GaussianSplatRenderMode.Splats or GaussianSplatRenderMode.PointCloud))
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
            var cs = _config?.CsSplatSort;
            if (cs == null) return false;
            if (_globalSorter == null || _globalSorterShader != cs)
            {
                _globalSorter = new GpuSorting(cs);
                _globalSorterShader = cs;
            }
            return _globalSorter != null && _globalSorter.Valid;
        }

        private bool EnsureGlobalCountingSorter()
        {
            var cs = _config?.CsCountingSort;
            if (cs == null) return false;
            if (_globalCountingSorter == null || _globalCountingSortShader != cs)
            {
                _globalCountingSorter?.Dispose();
                _globalCountingSorter = new GpuCountingSort(cs);
                _globalCountingSortShader = cs;
            }
            return _globalCountingSorter != null && _globalCountingSorter.Valid;
        }

        private bool EnsureGlobalGroupCache(int renderOrder, int splatCount, out GlobalOrderGroupCache cache)
        {
            cache = null;
            // Radix sort needs wave intrinsics; on platforms without them fall
            // back to the coarser counting sort instead of dropping the path.
            bool radixValid = EnsureGlobalSorter();
            if ((!radixValid && !EnsureGlobalCountingSorter()) || splatCount <= 0)
                return false;
            if (!radixValid)
                GpuCountingSort.LogFallbackOnce();

            if (!_globalGroups.TryGetValue(renderOrder, out cache))
            {
                cache = new GlobalOrderGroupCache();
                _globalGroups.Add(renderOrder, cache);
            }

            if (cache.viewData == null || cache.splatCapacity < splatCount)
            {
                Journal($"grow cache order={renderOrder} {cache.splatCapacity}->{splatCount}");
                // Retire, don't dispose: an earlier camera's draw recorded this
                // frame may still reference the old buffers at submit time —
                // immediate disposal makes Vulkan drop that draw ("none provided").
                cache.RetireBuffer(ref cache.viewData);
                cache.RetireBuffer(ref cache.sortDistances);
                cache.RetireBuffer(ref cache.sortKeys);
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

            if (cache.visibleCounter == null)
            {
                cache.visibleCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4)
                {
                    name = $"GaussianGlobalVisibleCounter_{renderOrder}"
                };
                cache.indirectArgs = new GraphicsBuffer(
                    GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, 5, 4)
                {
                    name = $"GaussianGlobalIndirectArgs_{renderOrder}"
                };
                cache.indirectArgsValid = false;
            }

            // Ping-pong support buffers are only needed by the radix sorter.
            if (radixValid &&
                (cache.sorterArgs.resources.altBuffer == null || cache.sorterArgs.count < (uint)splatCount))
            {
                cache.sorterArgs.resources.Dispose();
                cache.sorterArgs.resources = GpuSorting.SupportResources.Load((uint)splatCount);
            }

            cache.sorterArgs.inputKeys = cache.sortDistances;
            cache.sorterArgs.inputValues = cache.sortKeys;
            cache.sorterArgs.count = (uint)splatCount;
            // Distance keys: low 16 bits are sub-bucket noise, skip them (2x faster).
            cache.sorterArgs.sortStartBit = 16;
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
            // PointCloud reads the same sorted view data as Splats, so it shares
            // this path; only the display material and point params differ.
            bool pointCloudMode = _config.renderMode == GaussianSplatRenderMode.PointCloud;
            Material modeDisplayMat = pointCloudMode ? _matPointCloud : _matSplats;
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
                int minSortNth = int.MaxValue;
                for (int i = groupStart; i < groupEnd; ++i)
                {
                    var gs = _activeSplats[i].Item1;
                    groupSignature = groupSignature * 31 + gs.GetInstanceID();
                    groupSignature = groupSignature * 31 + gs.EffectiveSplatCount;
                    minSortNth = Mathf.Min(minSortNth, Mathf.Max(1, gs.sortNthFrame));
                }
                // Sorted keys are only valid for the camera that sorted them: a
                // different camera must re-sort regardless of the throttle, and
                // the throttle counts real frames, not per-camera renders.
                bool groupSortNeeded = !groupCache.hasSortedKeys
                    || groupCache.lastSortCameraId != cam.GetInstanceID()
                    || Time.frameCount - groupCache.lastSortFrame >= minSortNth;
                if (groupCache.groupSignature != groupSignature)
                {
                    groupCache.hasSortedKeys = false;
                    groupSortNeeded = true;
                }

                if (groupSortNeeded)
                    cmb.SetBufferData(groupCache.visibleCounter, s_zeroCounter);

                Material groupDisplayMat = null;
                int dstOffset = 0;
                bool groupValid = true;
                for (int i = groupStart; i < groupEnd; ++i)
                {
                    var gs = _activeSplats[i].Item1;
                    matComposite = _matComposite;
                    groupDisplayMat ??= modeDisplayMat;
                    if (modeDisplayMat == null)
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
                        groupCache.visibleCounter, 0, dstOffset, gs.EffectiveSplatCount, groupSortNeeded);
                    if (!copySuccess)
                    {
                        groupValid = false;
                        break;
                    }

                    dstOffset += gs.EffectiveSplatCount;
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
                _tileRenderer ??= new GaussianTileRenderer();
                // Prefer the actual RT size supplied by the render feature;
                // cam.pixelWidth/Height disagree with the RT under URP render
                // scale != 1 or dynamic resolution.
                int tileW = _tileRenderer.TileRenderSize.x > 0 ? _tileRenderer.TileRenderSize.x : cam.pixelWidth;
                int tileH = _tileRenderer.TileRenderSize.y > 0 ? _tileRenderer.TileRenderSize.y : cam.pixelHeight;
                if (!pointCloudMode && _config.useTileRenderer && _tileRenderer.HasOutputTarget &&
                    _tileRenderer.EnsureResources(_config, groupCache, tileW, tileH))
                {
                    _tileRenderer.Dispatch(cmb, cam, groupCache, dstOffset, groupSortNeeded, tileW, tileH);
                    usedTile = true;
                    Journal($"tile cam={cam.name} count={dstOffset}");
                }

                if (!usedTile && groupSortNeeded)
                {
                    if (_globalSorter != null && _globalSorter.Valid)
                    {
                        groupCache.sorterArgs.count = (uint)dstOffset;
                        _globalSorter.Dispatch(cmb, groupCache.sorterArgs);
                    }
                    else if (_globalCountingSorter != null && _globalCountingSorter.Valid)
                    {
                        _globalCountingSorter.Dispatch(cmb, (uint)dstOffset,
                            groupCache.sortDistances, groupCache.sortKeys);
                    }
                    groupCache.hasSortedKeys = true;
                    groupCache.lastSortCameraId = cam.GetInstanceID();
                    groupCache.lastSortFrame = Time.frameCount;
                    groupCache.indirectArgsValid = DispatchWriteIndirectArgs(cmb, reference,
                        groupCache.visibleCounter, groupCache.indirectArgs);
                }

                // ── Fallback: traditional DrawProcedural ───────────────
                if (!usedTile && BuffersValidForDraw("global", groupCache.viewData, groupCache.sortKeys))
                {
                    Journal($"draw global cam={cam.name} count={dstOffset} " +
                            $"view#{groupCache.viewData.GetHashCode():X8} order#{groupCache.sortKeys.GetHashCode():X8}");
                    _globalMpb.Clear();
                    _globalMpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, groupCache.viewData);
                    _globalMpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, groupCache.sortKeys);
                    _globalMpb.SetInteger(GaussianSplatRenderer.Props.IsStereo, 0);
                    if (pointCloudMode)
                    {
                        _globalMpb.SetFloat(GaussianSplatRenderer.Props.PointSizeScale, _config.pointCloudSizeScale);
                        _globalMpb.SetFloat(GaussianSplatRenderer.Props.PointMinSize, _config.pointCloudMinDisplaySize);
                        _globalMpb.SetFloat(GaussianSplatRenderer.Props.PointMinWorldSize, _config.pointCloudMinWorldSize);
                        _globalMpb.SetFloat(GaussianSplatRenderer.Props.PointProjectionScale, cam.projectionMatrix.m11);
                        _globalMpb.SetFloat(GaussianSplatRenderer.Props.PointMaxSize, _config.pointCloudMaxDisplaySize);
                        _globalMpb.SetFloat(GaussianSplatRenderer.Props.PointOpacityBoost, _config.pointCloudOpacityBoost);
                    }
                    if (groupCache.indirectArgsValid)
                        cmb.DrawProceduralIndirect(reference.GpuIndexBuffer, Matrix4x4.identity,
                            groupDisplayMat, 0, MeshTopology.Triangles, groupCache.indirectArgs, 0, _globalMpb);
                    else
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

                var matrix = gs.transform.localToWorldMatrix;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = _config.renderMode switch
                {
                    GaussianSplatRenderMode.DebugPoints => _matDebugPoints,
                    GaussianSplatRenderMode.DebugPointIndices => _matDebugPoints,
                    GaussianSplatRenderMode.DebugBoxes => _matDebugBoxes,
                    GaussianSplatRenderMode.DebugChunkBounds => _matDebugBoxes,
                    GaussianSplatRenderMode.PointCloud => _matPointCloud,
                    _ => _matSplats
                };
                if (displayMat == null)
                    continue;

                if (!BuffersValidForDraw($"per-object:{gs.name}", gs.GpuView, gs.GpuSortKeys))
                    continue;

                Journal($"draw per-object {gs.name} cam={cam.name} count={gs.EffectiveSplatCount} " +
                        $"view#{gs.GpuView.GetHashCode():X8} order#{gs.GpuSortKeys.GetHashCode():X8}");
                gs.SetAssetDataOnMaterial(mpb);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatChunks, gs.GpuChunksBuffer);
                mpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, gs.GpuView);
                mpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, gs.GpuSortKeys);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatScale, gs.splatScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatOpacityScale, gs.opacityScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.SplatSize, _config.pointDisplaySize);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointSizeScale, _config.pointCloudSizeScale);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointMinSize, _config.pointCloudMinDisplaySize);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointMinWorldSize, _config.pointCloudMinWorldSize);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointProjectionScale, cam.projectionMatrix.m11);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointMaxSize, _config.pointCloudMaxDisplaySize);
                mpb.SetFloat(GaussianSplatRenderer.Props.PointOpacityBoost, _config.pointCloudOpacityBoost);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.shOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.shOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, _config.renderMode == GaussianSplatRenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, _config.renderMode == GaussianSplatRenderMode.DebugChunkBounds ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.IsStereo, 0);

                cmb.BeginSample(ProfCalcView);
                bool calcSuccess = gs.CalcViewData(cmb, cam);
                cmb.EndSample(ProfCalcView);
                if (!calcSuccess)
                    continue;

                // Sort after view data so distance keys can read visibility
                // (invisible splats sink to the tail; indirect draw skips them).
                if (gs.NeedsSort(cam))
                {
                    gs.SortPoints(cmb, cam, matrix);
                    gs.MarkSorted(cam);
                }

                // draw
                int indexCount = 6;
                int instanceCount = gs.EffectiveSplatCount;
                MeshTopology topology = MeshTopology.Triangles;
                if (_config.renderMode is GaussianSplatRenderMode.DebugBoxes or GaussianSplatRenderMode.DebugChunkBounds)
                    indexCount = 36;
                if (_config.renderMode == GaussianSplatRenderMode.DebugChunkBounds)
                    instanceCount = gs.GpuChunksValid ? gs.GpuChunksBuffer.count : 0;

                cmb.BeginSample(ProfDraw);
                if (_config.renderMode == GaussianSplatRenderMode.Splats && gs.IndirectDrawReady)
                    cmb.DrawProceduralIndirect(gs.GpuIndexBuffer, matrix, displayMat, 0, topology,
                        gs.GpuIndirectArgs, 0, mpb);
                else
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
