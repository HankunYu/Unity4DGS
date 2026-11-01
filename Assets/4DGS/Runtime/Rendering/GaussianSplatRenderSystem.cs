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
        // Global path only: merging each renderer's view data into the group buffers.
        internal static readonly ProfilerMarker ProfCopyView = new(ProfilerCategory.Render, "GaussianSplat.CopyView", MarkerFlags.SampleGPU);
        // Sort plus the indirect-args write that follows it, on every path.
        internal static readonly ProfilerMarker ProfSort = new(ProfilerCategory.Render, "GaussianSplat.Sort", MarkerFlags.SampleGPU);
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
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                // OnDisable runs after this event during the domain unload;
                // anything deferred from then on would never be flushed.
                s_tearingDown = true;
                FlushDeferredDisposal(true);
            };
            // Vulkan binding errors are native render-thread logs with no C#
            // stack — flag them here, dump the draw journal on the main thread.
            Application.logMessageReceivedThreaded += (msg, _, _) =>
            {
                if (msg.StartsWith("Vulkan: Shader requires a compute buffer"))
                    s_bindingErrorSeen = true;
            };
#endif
            Application.quitting += () =>
            {
                s_tearingDown = true;
                FlushDeferredDisposal(true);
            };
        }

        static bool s_tearingDown;

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
            if (s_tearingDown)
            {
                // No later frame will flush the queue; nothing renders anymore.
                buf.Dispose();
                return;
            }
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
        IGpuSorter _globalSorter;
        GaussianSplatConfig _globalSorterConfig;
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

        private static readonly int PropSrcBlend = Shader.PropertyToID("_SrcBlend");
        private static readonly int PropDstBlend = Shader.PropertyToID("_DstBlend");
        private static readonly int PropBlendOp = Shader.PropertyToID("_BlendOp");
        private static readonly int PropAovDepthScale = Shader.PropertyToID("_AovDepthScale");
        private static readonly int PropSplatQuadExtent = Shader.PropertyToID("_SplatQuadExtent");
        private static readonly int PropSplatAdaptiveExtent = Shader.PropertyToID("_SplatAdaptiveExtent");
        private static readonly int PropSplatMaxPixelRadius = Shader.PropertyToID("_SplatMaxPixelRadius");
        private static readonly int PropQuadBaseShape = Shader.PropertyToID("_QuadBaseShape");
        private static readonly int PropSplatAlphaCutoff = Shader.PropertyToID("_SplatAlphaCutoff");

        // Fragments contributing less than this are dropped, judged by the
        // larger of the light they block and the light they add
        // (SplatContribution; splats are culled at the default before any
        // blur, SPLAT_CULL_ALPHA). Depth of field spreads a
        // splat's light over its blur circle, and a blurred surface is many
        // faint splats adding up: a small splat blurred 24 pixels wide keeps
        // under a thousandth of its opacity, so it draws far fainter ones.
        private const float DefaultAlphaCutoff = 5f / 255f;
        private const float DepthOfFieldAlphaCutoff = 1f / 16384f;

        private static float AlphaCutoff(GaussianDepthOfField dof) =>
            dof != null ? DepthOfFieldAlphaCutoff : DefaultAlphaCutoff;

        // Per-render state shared by every splat draw: quad shaping, the
        // camera's alpha cutoff, and the beauty/AOV switch of the point pass
        // and the composite.
        internal void ApplyOutputState(CommandBuffer cmb, Camera cam)
        {
            cmb.SetGlobalFloat(PropSplatAlphaCutoff, AlphaCutoff(GaussianDepthOfField.ActiveOn(cam)));
            if (_config != null)
            {
                // The fragment falloff is exp(-dot(pos,pos)): one unit of quad
                // extent is sqrt(2) standard deviations.
                cmb.SetGlobalFloat(PropSplatQuadExtent, _config.splatQuadSigma / Mathf.Sqrt(2f));
                cmb.SetGlobalInteger(PropSplatAdaptiveExtent, _config.adaptiveSplatExtent ? 1 : 0);
                cmb.SetGlobalFloat(PropSplatMaxPixelRadius, _config.maxSplatPixelRadius);
                cmb.SetGlobalInteger(PropQuadBaseShape, _config.renderMode == GaussianSplatRenderMode.PointCloud ? 1 : 0);
            }
            ApplyAovState(cmb);
        }

        // Switches the point pass and the composite between beauty and AOV.
        //
        // The AOV path resolves nearest-sample through the blend unit — the
        // splat render target has no depth buffer, and depth is not something
        // an alpha blend can average. BlendOp Max over reciprocal distance gets
        // the same result order-independently.
        //
        // Keywords go through the command buffer, not Material.EnableKeyword:
        // the latter is CPU-immediate and would be cleared again before the
        // render graph executes the deferred draw.
        private void ApplyAovState(CommandBuffer cmb)
        {
            bool aov = _config != null &&
                       _config.pointCloudAov != GaussianSplatAovMode.None &&
                       _config.renderMode == GaussianSplatRenderMode.PointCloud;

            if (aov)
            {
                cmb.EnableShaderKeyword("GAUSSIAN_POINT_AOV");
                cmb.EnableShaderKeyword("GAUSSIAN_AOV");
            }
            else
            {
                cmb.DisableShaderKeyword("GAUSSIAN_POINT_AOV");
                cmb.DisableShaderKeyword("GAUSSIAN_AOV");
            }

            if (_matStylized != null)
            {
                _matStylized.SetFloat(PropSrcBlend, (float)(aov ? BlendMode.One : BlendMode.OneMinusDstAlpha));
                _matStylized.SetFloat(PropDstBlend, (float)BlendMode.One);
                _matStylized.SetFloat(PropBlendOp, (float)(aov ? BlendOp.Max : BlendOp.Add));
            }
            if (_matComposite != null)
            {
                _matComposite.SetFloat(PropSrcBlend, (float)BlendMode.One);
                _matComposite.SetFloat(PropDstBlend, (float)(aov ? BlendMode.Zero : BlendMode.OneMinusSrcAlpha));
                _matComposite.SetFloat(PropAovDepthScale,
                    _config != null ? Mathf.Max(_config.pointCloudAovDepthScale, 1e-4f) : 1.0f);
            }
        }

        // ── Materials (created from Config shaders) ─────────────────────
        private Material _matSplats;
        private Material _matComposite;
        private Material _matDebugPoints;
        private Material _matDebugBoxes;
        private Material _matStylized;
        private GaussianSplatConfig _materialSource;

        internal Material MatSplats => _matSplats;
        internal Material MatComposite => _matComposite;
        internal Material MatDebugPoints => _matDebugPoints;
        internal Material MatDebugBoxes => _matDebugBoxes;
        internal Material MatStylized => _matStylized;

        internal void EnsureMaterials()
        {
            if (_config == null) return;
            if (_matSplats != null && _materialSource == _config) return;

            Object.DestroyImmediate(_matSplats);
            Object.DestroyImmediate(_matComposite);
            Object.DestroyImmediate(_matDebugPoints);
            Object.DestroyImmediate(_matDebugBoxes);
            Object.DestroyImmediate(_matStylized);

            _matSplats = new Material(_config.ShaderSplats) { name = "GaussianSplats" };
            _matComposite = new Material(_config.ShaderComposite) { name = "GaussianClearDstAlpha" };
            _matDebugPoints = new Material(_config.ShaderDebugPoints) { name = "GaussianDebugPoints" };
            _matDebugBoxes = new Material(_config.ShaderDebugBoxes) { name = "GaussianDebugBoxes" };
            _matStylized = new Material(_config.ShaderStylized) { name = "GaussianStylized" };

            _materialSource = _config;
        }

        // Points, and styled splats, draw their baked quads with the quad
        // shader; every other mode is global
        private Material DisplayMaterial(GaussianSplatRenderer gs)
        {
            return _config.renderMode switch
            {
                GaussianSplatRenderMode.DebugPoints => _matDebugPoints,
                GaussianSplatRenderMode.DebugPointIndices => _matDebugPoints,
                GaussianSplatRenderMode.DebugBoxes => _matDebugBoxes,
                GaussianSplatRenderMode.DebugChunkBounds => _matDebugBoxes,
                _ => gs.QuadsBaked ? _matStylized : _matSplats
            };
        }

        // Draw-side style parameters of a per-object draw: a one-entry range
        // table. The style color itself is computed with the view data.
        private static readonly GaussianSplatStyleRange[] s_singleStyleRange = new GaussianSplatStyleRange[1];

        private static void SetStyleOnMaterial(CommandBuffer cmb, GaussianSplatRenderer gs, MaterialPropertyBlock mpb)
        {
            if (!gs.QuadsBaked)
                return;
            s_singleStyleRange[0] = gs.StyleRange(0);
            // Recorded into the command buffer, so it lands before this
            // renderer's draw however the draws are batched
            cmb.SetBufferData(gs.StyleRangeBuffer, s_singleStyleRange);
            mpb.SetBuffer(GaussianSplatRenderer.Props.StyleRanges, gs.StyleRangeBuffer);
            mpb.SetInteger(GaussianSplatRenderer.Props.StyleRangeCount, 1);
        }

        // Range table of the global group being built, one entry per member
        private GaussianSplatStyleRange[] _styleRanges = new GaussianSplatStyleRange[4];
        private int _styleRangeCount;

        private void AddStyleRange(GaussianSplatStyleRange range)
        {
            if (_styleRangeCount == _styleRanges.Length)
                System.Array.Resize(ref _styleRanges, _styleRanges.Length * 2);
            _styleRanges[_styleRangeCount++] = range;
        }

        private void SetGroupStyleRanges(CommandBuffer cmb, GlobalOrderGroupCache cache, MaterialPropertyBlock mpb)
        {
            if (cache.styleRanges == null || cache.styleRanges.count < _styleRangeCount)
            {
                // Retire: a draw recorded earlier this frame may still bind it
                cache.RetireBuffer(ref cache.styleRanges);
                cache.styleRanges = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    _styleRanges.Length, GaussianSplatStyleRange.Stride) { name = "GaussianGlobalStyleRanges" };
            }
            cmb.SetBufferData(cache.styleRanges, _styleRanges, 0, 0, _styleRangeCount);
            mpb.SetBuffer(GaussianSplatRenderer.Props.StyleRanges, cache.styleRanges);
            mpb.SetInteger(GaussianSplatRenderer.Props.StyleRangeCount, _styleRangeCount);
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
            public int pass;
        }

        // Splats and PointCloud draw the sorted view data: invisible splats sit
        // past the indirect instance count, and the direct pass applies. The
        // debug modes draw every splat (or chunk) through pass 0.
        private bool UsesSortedViewData =>
            _config.renderMode is GaussianSplatRenderMode.Splats or GaussianSplatRenderMode.PointCloud;

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

        // Per-render view/projection supplied by SRP features when the pipeline
        // renders a camera with matrices that never touch the Camera object
        // (e.g. cubemap faces during 360 capture). Null = read the Camera.
        public struct RenderMatrixOverride
        {
            public Matrix4x4 View;
            public Matrix4x4 Proj;
            public Vector2Int ScreenSize;
        }

        public RenderMatrixOverride? MatrixOverride { get; set; }

        // ── Composite mode ────────────────────────────────────────────
        // Set by the render feature for the render in flight: draw straight
        // into the camera target (pass 1, back-to-front) instead of the
        // intermediate splat RT (pass 0, front-to-back).
        public bool DirectToTarget { get; set; }

        // Shader pass for the Splats and PointCloud display materials.
        internal int DisplayPass => DirectToTarget ? 1 : 0;

        // Whether a render may take the direct path. Everything that needs the
        // intermediate RT — a Gaussian-only stylize pass, AOVs, the debug
        // modes, ODS (no meaningful depth) — or a backbuffer target (whose Y
        // orientation the splat projection does not handle) stays offscreen.
        public bool CanRenderDirect(bool stylizeSplatsOnly, bool targetIsBackBuffer)
        {
            var config = Config;
            return config != null &&
                   config.compositeMode == GaussianSplatCompositeMode.Direct &&
                   config.renderMode is GaussianSplatRenderMode.Splats or GaussianSplatRenderMode.PointCloud &&
                   config.pointCloudAov == GaussianSplatAovMode.None &&
                   config.projectionMode == GaussianSplatProjectionMode.Perspective &&
                   !stylizeSplatsOnly && !targetIsBackBuffer;
        }

        // Sort settings for the render in flight. Throttling needs keys that
        // stay valid while the camera turns (radial metric) and content that
        // does not move by itself; edit mode always sorts, since GPU-side
        // edits are not all tracked.
        private GaussianSplatSortState.Settings SortSettings(bool animatedContent)
        {
            bool radial = _config.UseRadialSort;
            return new GaussianSplatSortState.Settings
            {
                Radial = radial,
                BackToFront = DirectToTarget,
                Throttled = Application.isPlaying && radial && _config.sortMoveThreshold > 0f &&
                            !animatedContent && !MatrixOverride.HasValue,
            };
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
            public readonly GaussianSplatSortState sortState = new();
            public int lastUsedFrame;
            public GraphicsBuffer viewData;
            public GraphicsBuffer sortDistances;
            public GraphicsBuffer sortKeys;
            public GraphicsBuffer visibleCounter;   // [0] = visible splats this sort
            public GraphicsBuffer indirectArgs;     // 5 uints for DrawProceduralIndirect
            public bool indirectArgsValid;          // args written for the current keys
            public GpuSortArgs sorterArgs;
            public int sorterCapacity;              // splats the sort scratch buffers fit
            public GraphicsBuffer styleRanges;      // GaussianSplatStyleRange per member renderer

            // ── Tile-based rendering buffers ──────────────────────────────
            public const int MaxTilePairs = 16 * 1024 * 1024; // 16M (tile,splat) pairs
            public GraphicsBuffer tileKeys;         // sort key per pair: (tile_id<<16)|depth
            public GraphicsBuffer tileValues;       // splatId per pair
            public GraphicsBuffer tileRanges;       // uint2[numTiles]: (start,end) per tile
            public GraphicsBuffer tilePairCounter;  // 4-byte atomic counter
            public GpuSortArgs tileSorterArgs;
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
                DeferDispose(styleRanges);   styleRanges = null;
                indirectArgsValid = false;
                sorterArgs.resources.Dispose();
                sorterCapacity = 0;

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
            _globalSorterConfig = null;
            _tileRenderer = null;
            _hasRenderFence = false;

            Object.DestroyImmediate(_matSplats);
            Object.DestroyImmediate(_matComposite);
            Object.DestroyImmediate(_matDebugPoints);
            Object.DestroyImmediate(_matDebugBoxes);
            Object.DestroyImmediate(_matStylized);
            _matSplats = null;
            _matComposite = null;
            _matDebugPoints = null;
            _matDebugBoxes = null;
            _matStylized = null;
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
                if (gs == null || !gs.isActiveAndEnabled || !gs.visible || !gs.HasValidAsset || !gs.HasValidRenderSetup)
                    continue;
                gs.RefreshStyle();
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

        // Gather sorts front to back, the order the offscreen under-blend
        // composites in; the direct over-blend needs the reverse, so a nearer
        // object (and a higher render order) lands on top in both
        private (GaussianSplatRenderer, MaterialPropertyBlock) ActiveSplatInDrawOrder(int i)
        {
            return _activeSplats[DirectToTarget ? _activeSplats.Count - 1 - i : i];
        }

        public static int MaxTilePairsCapacity => GlobalOrderGroupCache.MaxTilePairs;

        // Latest tile pair count from async readback (for diagnostics / Inspector).
        public uint LastTilePairCount => _tileRenderer?.GetLastTilePairCount(_globalGroups) ?? 0;

        // ReSharper disable once MemberCanBePrivate.Global - used by HDRP/URP features that are not always compiled
        public Material SortAndRenderSplats(Camera cam, CommandBuffer cmb)
        {
            if (_hasRenderFence)
                cmb.WaitOnAsyncGraphicsFence(_lastRenderFence);

            ApplyOutputState(cmb, cam);

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

            ApplyOutputState(cmb, cam);

            _preparedItems.Clear();
            Material matComposite = _matComposite;

            for (int drawIdx = 0; drawIdx < _activeSplats.Count; drawIdx++)
            {
                var kvp = ActiveSplatInDrawOrder(drawIdx);
                var gs = kvp.Item1;
                var mpb = kvp.Item2;

                var matrix = gs.transform.localToWorldMatrix;

                mpb.Clear();
                Material displayMat = DisplayMaterial(gs);
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
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.EffectiveSHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.shOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex,
                    _config.renderMode == GaussianSplatRenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks,
                    _config.renderMode == GaussianSplatRenderMode.DebugChunkBounds ? 1 : 0);
                SetStyleOnMaterial(cmb, gs, mpb);

                cmb.BeginSample(ProfCalcView);
                bool calcSuccess = gs.CalcViewData(cmb, cam);
                cmb.EndSample(ProfCalcView);
                if (!calcSuccess)
                    continue;

                // Sort after view data so distance keys can read visibility
                // (invisible splats sink to the tail; indirect draw skips them).
                // Order-independent splats keep whatever order the keys hold
                // and draw them all: a culled splat is a degenerate quad.
                bool sorted = !gs.StyleOrderIndependent;
                var sortSettings = SortSettings(gs.HasAnimatedContent);
                if (sorted && gs.NeedsSort(cam, sortSettings))
                {
                    gs.SortPoints(cmb, cam, matrix, sortSettings);
                    gs.MarkSorted(cam, sortSettings);
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
                    indirectArgs = UsesSortedViewData && sorted && gs.IndirectDrawReady ? gs.GpuIndirectArgs : null,
                    pass = UsesSortedViewData ? DisplayPass : 0,
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
                        item.displayMat, item.pass, item.topology, item.indirectArgs, 0, item.mpb);
                else
                    cmb.DrawProcedural(item.gs.GpuIndexBuffer, item.gs.transform.localToWorldMatrix,
                        item.displayMat, item.pass, item.topology, item.indexCount, item.instanceCount, item.mpb);
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
            if (_config == null)
                return false;
            if (_globalSorterConfig != _config)
            {
                _globalSorter = GpuSorters.Create(_config.CsSplatSort, _config.CsPortableSort);
                _globalSorterConfig = _config;
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
                cache.sortState.Invalidate();
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

            // Capacity is tracked separately: sorterArgs.count is rewritten
            // with the live count every sort, so it cannot tell how big the
            // allocation is (growing/shrinking groups would realloc each time).
            if (cache.sorterArgs.resources.altKeys == null || cache.sorterCapacity < splatCount)
            {
                cache.sorterArgs.resources.Dispose();
                cache.sorterArgs.resources = GpuSortResources.Load((uint)splatCount);
                cache.sorterCapacity = splatCount;
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
            // this path; its points are baked quads, drawn by the quad shader.
            int groupStart = 0;
            while (groupStart < _activeSplats.Count)
            {
                int order = ActiveSplatInDrawOrder(groupStart).Item1.renderOrder;
                int groupEnd = groupStart + 1;
                int groupSplatCount = ActiveSplatInDrawOrder(groupStart).Item1.EffectiveSplatCount;
                while (groupEnd < _activeSplats.Count && ActiveSplatInDrawOrder(groupEnd).Item1.renderOrder == order)
                {
                    groupSplatCount += ActiveSplatInDrawOrder(groupEnd).Item1.EffectiveSplatCount;
                    ++groupEnd;
                }

                var reference = ActiveSplatInDrawOrder(groupStart).Item1;
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
                int contentKey = 17;
                int minSortNth = int.MaxValue;
                bool groupAnimated = false;
                // Any baked member makes the group draw with the quad shader,
                // which hands plain members back to the splat pass
                bool groupBaked = false;
                bool groupOrderIndependent = true;
                for (int i = groupStart; i < groupEnd; ++i)
                {
                    var gs = ActiveSplatInDrawOrder(i).Item1;
                    groupBaked |= gs.QuadsBaked;
                    groupOrderIndependent &= gs.StyleOrderIndependent;
                    groupSignature = groupSignature * 31 + gs.GetInstanceID();
                    groupSignature = groupSignature * 31 + gs.EffectiveSplatCount;
                    // Keys are sorted in world space: any member's data or
                    // transform changing makes them stale.
                    contentKey = contentKey * 31 + gs.ContentVersion;
                    contentKey = contentKey * 31 + gs.transform.localToWorldMatrix.GetHashCode();
                    minSortNth = Mathf.Min(minSortNth, Mathf.Max(1, gs.sortNthFrame));
                    groupAnimated |= gs.HasAnimatedContent;
                }
                if (groupCache.groupSignature != groupSignature)
                    groupCache.sortState.Invalidate();
                // A matrix override means the same camera renders multiple views
                // this frame (cubemap faces), so every render must re-sort.
                var sortSettings = SortSettings(groupAnimated);
                Vector3 camPos = cam.transform.position;
                // A fully additive group blends the same in any order: it skips
                // the sort and draws identity keys, rewritten every frame since
                // membership can change. The keys no longer hold a sorted order
                // for the next sorted frame.
                bool groupSortNeeded = !groupOrderIndependent && (MatrixOverride.HasValue ||
                    groupCache.sortState.NeedsSort(cam.GetInstanceID(), sortSettings.Key, contentKey, camPos,
                        minSortNth, sortSettings.Throttled, _config.sortMoveThreshold));
                bool writeKeys = groupSortNeeded || groupOrderIndependent;
                if (groupOrderIndependent)
                    groupCache.sortState.Invalidate();

                if (writeKeys)
                    cmb.SetBufferData(groupCache.visibleCounter, s_zeroCounter);

                Material groupDisplayMat = null;
                _styleRangeCount = 0;
                int dstOffset = 0;
                bool groupValid = true;
                for (int i = groupStart; i < groupEnd; ++i)
                {
                    var gs = ActiveSplatInDrawOrder(i).Item1;
                    matComposite = _matComposite;
                    groupDisplayMat ??= groupBaked ? _matStylized : _matSplats;
                    if (groupDisplayMat == null)
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

                    cmb.BeginSample(ProfCopyView);
                    bool copySuccess = gs.CopyViewDataAndDistances(cmb, cam, gs.transform.localToWorldMatrix,
                        groupCache.viewData, groupCache.sortDistances, groupCache.sortKeys,
                        groupCache.visibleCounter, 0, dstOffset, gs.EffectiveSplatCount, writeKeys,
                        sortSettings);
                    cmb.EndSample(ProfCopyView);
                    if (!copySuccess)
                    {
                        groupValid = false;
                        break;
                    }

                    if (groupBaked)
                        AddStyleRange(gs.StyleRange(dstOffset));
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

                // ── Tile-based rendering path ──────────────────────────
                bool usedTile = false;
                _tileRenderer ??= new GaussianTileRenderer();
                // Prefer the actual RT size supplied by the render feature;
                // cam.pixelWidth/Height disagree with the RT under URP render
                // scale != 1 or dynamic resolution.
                int tileW = _tileRenderer.TileRenderSize.x > 0 ? _tileRenderer.TileRenderSize.x : cam.pixelWidth;
                int tileH = _tileRenderer.TileRenderSize.y > 0 ? _tileRenderer.TileRenderSize.y : cam.pixelHeight;
                // The tile renderer composites the view data colors on its own
                // and knows nothing of the draw-side style parameters
                if (!groupBaked && !DirectToTarget && _config.useTileRenderer && _tileRenderer.HasOutputTarget &&
                    _tileRenderer.EnsureResources(_config, groupCache, tileW, tileH))
                {
                    // The tile pipeline sorts and rasterises in one; its GS.Tile*
                    // markers nest inside Draw.
                    cmb.BeginSample(ProfDraw);
                    _tileRenderer.Dispatch(cmb, cam, groupCache, dstOffset, groupSortNeeded, tileW, tileH);
                    cmb.EndSample(ProfDraw);
                    usedTile = true;
                    Journal($"tile cam={cam.name} count={dstOffset}");
                }

                if (!usedTile && groupSortNeeded)
                {
                    cmb.BeginSample(ProfSort);
                    groupCache.sorterArgs.count = (uint)dstOffset;
                    _globalSorter.Dispatch(cmb, groupCache.sorterArgs);
                    groupCache.sortState.MarkSorted(cam.GetInstanceID(), sortSettings.Key, contentKey, camPos);
                    groupCache.indirectArgsValid = DispatchWriteIndirectArgs(cmb, reference,
                        groupCache.visibleCounter, groupCache.indirectArgs);
                    cmb.EndSample(ProfSort);
                }

                // ── Fallback: traditional DrawProcedural ───────────────
                if (!usedTile && BuffersValidForDraw("global", groupCache.viewData, groupCache.sortKeys))
                {
                    cmb.BeginSample(ProfDraw);
                    Journal($"draw global cam={cam.name} count={dstOffset} " +
                            $"view#{groupCache.viewData.GetHashCode():X8} order#{groupCache.sortKeys.GetHashCode():X8}");
                    _globalMpb.Clear();
                    _globalMpb.SetBuffer(GaussianSplatRenderer.Props.SplatViewData, groupCache.viewData);
                    _globalMpb.SetBuffer(GaussianSplatRenderer.Props.OrderBuffer, groupCache.sortKeys);
                    _globalMpb.SetInteger(GaussianSplatRenderer.Props.IsStereo, 0);
                    if (groupBaked)
                        SetGroupStyleRanges(cmb, groupCache, _globalMpb);
                    if (groupCache.indirectArgsValid && !groupOrderIndependent)
                        cmb.DrawProceduralIndirect(reference.GpuIndexBuffer, Matrix4x4.identity,
                            groupDisplayMat, DisplayPass, MeshTopology.Triangles, groupCache.indirectArgs, 0, _globalMpb);
                    else
                        cmb.DrawProcedural(reference.GpuIndexBuffer, Matrix4x4.identity,
                            groupDisplayMat, DisplayPass, MeshTopology.Triangles, 6, dstOffset, _globalMpb);
                    cmb.EndSample(ProfDraw);
                }

                hasRenderedAGroup = true;
                groupStart = groupEnd;
            }
            ReleaseUnusedGlobalGroups(activeFrameId);
            return matComposite;
        }


        Material SortAndRenderSplatsPerObject(Camera cam, CommandBuffer cmb)
        {
            Material matComposite = _matComposite;
            for (int drawIdx = 0; drawIdx < _activeSplats.Count; drawIdx++)
            {
                var kvp = ActiveSplatInDrawOrder(drawIdx);
                var gs = kvp.Item1;
                var mpb = kvp.Item2;

                var matrix = gs.transform.localToWorldMatrix;

                // cache view
                kvp.Item2.Clear();
                Material displayMat = DisplayMaterial(gs);
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
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOrder, gs.EffectiveSHOrder);
                mpb.SetInteger(GaussianSplatRenderer.Props.SHOnly, gs.shOnly ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayIndex, _config.renderMode == GaussianSplatRenderMode.DebugPointIndices ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.DisplayChunks, _config.renderMode == GaussianSplatRenderMode.DebugChunkBounds ? 1 : 0);
                mpb.SetInteger(GaussianSplatRenderer.Props.IsStereo, 0);
                SetStyleOnMaterial(cmb, gs, mpb);

                cmb.BeginSample(ProfCalcView);
                bool calcSuccess = gs.CalcViewData(cmb, cam);
                cmb.EndSample(ProfCalcView);
                if (!calcSuccess)
                    continue;

                // Sort after view data so distance keys can read visibility
                // (invisible splats sink to the tail; indirect draw skips them).
                // A matrix override means the same camera renders multiple views
                // this frame (cubemap faces), so every render must re-sort.
                // Order-independent splats keep whatever order the keys hold
                // and draw them all: a culled splat is a degenerate quad.
                bool sorted = !gs.StyleOrderIndependent;
                var sortSettings = SortSettings(gs.HasAnimatedContent);
                if (sorted && (MatrixOverride.HasValue || gs.NeedsSort(cam, sortSettings)))
                {
                    gs.SortPoints(cmb, cam, matrix, sortSettings);
                    gs.MarkSorted(cam, sortSettings);
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
                int pass = UsesSortedViewData ? DisplayPass : 0;
                if (UsesSortedViewData && sorted && gs.IndirectDrawReady)
                    cmb.DrawProceduralIndirect(gs.GpuIndexBuffer, matrix, displayMat, pass, topology,
                        gs.GpuIndirectArgs, 0, mpb);
                else
                    cmb.DrawProcedural(gs.GpuIndexBuffer, matrix, displayMat, pass, topology, indexCount, instanceCount, mpb);
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
