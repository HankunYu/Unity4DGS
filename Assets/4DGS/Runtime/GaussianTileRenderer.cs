// SPDX-License-Identifier: MIT

using System.Collections.Generic;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    internal class GaussianTileRenderer
    {
        // ── Profiler markers ─────────────────────────────────────────────
        internal static readonly ProfilerMarker ProfTileClear  = new(ProfilerCategory.Render, "GS.TileClear",  MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker ProfTileAssign = new(ProfilerCategory.Render, "GS.TileAssign", MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker ProfTileSort   = new(ProfilerCategory.Render, "GS.TileSort",   MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker ProfTileBuild  = new(ProfilerCategory.Render, "GS.TileBuild",  MarkerFlags.SampleGPU);
        internal static readonly ProfilerMarker ProfTileRender = new(ProfilerCategory.Render, "GS.TileRender", MarkerFlags.SampleGPU);

        // ── Shader property IDs for tile rendering ───────────────────────
        private static class TileProps
        {
            public static readonly int SplatCount       = Shader.PropertyToID("_SplatCount");
            public static readonly int TileCountX       = Shader.PropertyToID("_TileCountX");
            public static readonly int TileCountY       = Shader.PropertyToID("_TileCountY");
            public static readonly int MaxTilePairs     = Shader.PropertyToID("_MaxTilePairs");
            public static readonly int ScreenParams     = Shader.PropertyToID("_TileScreenParams");
            public static readonly int SplatViewData    = Shader.PropertyToID("_SplatViewData");
            public static readonly int TilePairCounter  = Shader.PropertyToID("_TilePairCounter");
            public static readonly int TileKeys         = Shader.PropertyToID("_TileKeys");
            public static readonly int TileValues       = Shader.PropertyToID("_TileValues");
            public static readonly int TilePairCount    = Shader.PropertyToID("_TilePairCount");
            public static readonly int NumTiles         = Shader.PropertyToID("_NumTiles");
            public static readonly int SortedTileKeys   = Shader.PropertyToID("_SortedTileKeys");
            public static readonly int TileRanges       = Shader.PropertyToID("_TileRanges");
            public static readonly int SortedTileValues = Shader.PropertyToID("_SortedTileValues");
            public static readonly int TileRangesRead   = Shader.PropertyToID("_TileRangesRead");
            public static readonly int OutputTexture    = Shader.PropertyToID("_OutputTexture");
            public static readonly int ClearTileKeys    = Shader.PropertyToID("_ClearTileKeys");
            public static readonly int ClearTileRanges  = Shader.PropertyToID("_ClearTileRanges");
            public static readonly int ClearKeyCount    = Shader.PropertyToID("_ClearKeyCount");
            public static readonly int ClearRangeCount  = Shader.PropertyToID("_ClearRangeCount");
            public static readonly int TileShift        = Shader.PropertyToID("_TileShift");
        }

        // ── Public properties ────────────────────────────────────────────
        // Set by URP/HDRP feature before SortAndRenderSplats; tile renderer
        // writes directly here (avoids SetRenderTarget/Blit inside render graph).
        public RenderTargetIdentifier TileOutputTarget;
        // Actual render target dimensions (may differ from cam.pixelWidth/Height
        // when URP render scale != 1 or dynamic resolution is active).
        // Set by URP/HDRP feature; (0,0) means fall back to cam.pixelWidth/Height.
        public Vector2Int TileRenderSize;

        // ── Private fields ───────────────────────────────────────────────
        private GpuSorting _tileSorter;
        private ComputeShader _tileCs;
        private int _kernelClearTileData = -1;
        private int _kernelTileAssign    = -1;
        private int _kernelBuildRanges   = -1;
        private int _kernelRenderTiles   = -1;

        private static readonly uint[] s_CounterReadback = new uint[1];

        // ── Public methods ───────────────────────────────────────────────

        public bool EnsureResources(GaussianSplatConfig config,
            GaussianSplatRenderSystem.GlobalOrderGroupCache cache,
            int screenW, int screenH)
        {
            if (_tileCs == null)
            {
                _tileCs = config.CsTileRender;
                if (_tileCs == null) return false;
                _kernelClearTileData = _tileCs.FindKernel("CSClearTileData");
                _kernelTileAssign    = _tileCs.FindKernel("CSGaussianTileAssign");
                _kernelBuildRanges   = _tileCs.FindKernel("CSBuildTileRanges");
                _kernelRenderTiles   = _tileCs.FindKernel("CSRenderTiles");
            }
            if (_tileCs == null) return false;

            int tileX = (screenW + 15) / 16;
            int tileY = (screenH + 15) / 16;
            int numTiles = tileX * tileY;

            if (cache.tileKeys == null || cache.tileKeys.count < GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs)
            {
                cache.tileKeys?.Dispose();
                cache.tileValues?.Dispose();
                cache.tileKeys   = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs, 4) { name = "TileKeys" };
                cache.tileValues = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs, 4) { name = "TileValues" };
            }

            // Separate tile-count tracking from buffer allocation to avoid
            // use-after-free when Game + Scene cameras have different resolutions.
            if (cache.tileCountX != tileX || cache.tileCountY != tileY)
                cache.hasTileRanges = false;
            cache.tileCountX = tileX;
            cache.tileCountY = tileY;

            if (cache.tileRanges == null || numTiles > cache.tileRangesCapacity)
            {
                // Defer disposal: other cameras' CommandBuffers may still reference
                // the old buffer this frame. Clean up next frame instead.
                if (cache.tileRanges != null)
                    cache.retiredTileRanges.Add(cache.tileRanges);
                cache.tileRanges = new GraphicsBuffer(GraphicsBuffer.Target.Structured,
                    numTiles, 8) { name = "TileRanges" };
                cache.tileRangesCapacity = numTiles;
                cache.hasTileRanges = false;
            }

            if (cache.tilePairCounter == null)
                cache.tilePairCounter = new GraphicsBuffer(
                    GraphicsBuffer.Target.Raw, 1, 4) { name = "TilePairCounter" };

            if (cache.tileSorterArgs.resources.altBuffer == null ||
                cache.tileSorterArgs.count < (uint)GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs)
            {
                cache.tileSorterArgs.resources.Dispose();
                cache.tileSorterArgs.resources =
                    GpuSorting.SupportResources.Load((uint)GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs);
            }
            _tileSorter ??= new GpuSorting(config.CsSplatUtilities);

            return true;
        }

        public void Dispatch(CommandBuffer cmb, Camera cam,
            GaussianSplatRenderSystem.GlobalOrderGroupCache cache, int splatCount, bool sortNeeded,
            int screenW, int screenH)
        {
            int tileX    = cache.tileCountX;
            int tileY    = cache.tileCountY;
            int numTiles = tileX * tileY;
            int tileShift = CalcTileShift(numTiles);

            // Set _TileShift globally — used by assign, build-ranges, and render kernels.
            cmb.SetComputeIntParam(_tileCs, TileProps.TileShift, tileShift);

            // Invalidate tile ranges when the camera changes (e.g. Scene View vs Game
            // camera) — stale tile assignments from a different projection are wrong.
            int camId = cam.GetInstanceID();
            if (cache.lastTileCameraId != camId)
            {
                cache.hasTileRanges = false;
                cache.lastTileCameraId = camId;
                // Per-camera asyncTilePairCounts are keyed by camera ID, so
                // no reset needed on camera switch.
            }

            // On hit frames (no sort needed), skip assign/sort/build — just re-render.
            // Always run full pipeline if tile ranges haven't been built yet.
            if (sortNeeded || !cache.hasTileRanges)
            {
                // ── Sort budget from async readback (1-frame lag) ─────────
                // Use per-camera pair count so each camera gets an accurate
                // sort budget independent of other cameras.
                // First frame (no readback yet) sorts the full buffer to guarantee
                // correctness on all GPUs (NVIDIA needs the large budget).
                cache.asyncTilePairCounts.TryGetValue(camId, out uint prevCount);
                uint sortCount;
                if (prevCount == 0)
                    sortCount = (uint)GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs;
                else
                    sortCount = (uint)Mathf.Min((long)prevCount * 2, GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs);

                // ── GPU clear: keys → 0xFFFFFFFF, ranges → (0,0) ─────────
                cmb.BeginSample(ProfTileClear);
                int clearCount = Mathf.Max((int)sortCount, numTiles);
                int clearGroups = (clearCount + 1023) / 1024;
                cmb.SetComputeIntParam   (_tileCs, TileProps.ClearKeyCount,   (int)sortCount);
                cmb.SetComputeIntParam   (_tileCs, TileProps.ClearRangeCount, numTiles);
                cmb.SetComputeBufferParam(_tileCs, _kernelClearTileData, TileProps.ClearTileKeys,  cache.tileKeys);
                cmb.SetComputeBufferParam(_tileCs, _kernelClearTileData, TileProps.ClearTileRanges, cache.tileRanges);
                cmb.DispatchCompute(_tileCs, _kernelClearTileData, clearGroups, 1, 1);

                // Clear atomic counter
                s_CounterReadback[0] = 0;
                cmb.SetBufferData(cache.tilePairCounter, s_CounterReadback, 0, 0, 1);
                cmb.EndSample(ProfTileClear);

                // ── Kernel 1: assign each Gaussian to tiles ───────────────
                cmb.BeginSample(ProfTileAssign);
                cmb.SetComputeIntParam   (_tileCs, TileProps.SplatCount,   splatCount);
                cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountX,   tileX);
                cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountY,   tileY);
                cmb.SetComputeIntParam   (_tileCs, TileProps.MaxTilePairs, GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs);
                cmb.SetComputeVectorParam(_tileCs, TileProps.ScreenParams,
                    new Vector4(screenW, screenH, 1f / screenW, 1f / screenH));
                cmb.SetComputeBufferParam(_tileCs, _kernelTileAssign, TileProps.SplatViewData,   cache.viewData);
                cmb.SetComputeBufferParam(_tileCs, _kernelTileAssign, TileProps.TilePairCounter, cache.tilePairCounter);
                cmb.SetComputeBufferParam(_tileCs, _kernelTileAssign, TileProps.TileKeys,        cache.tileKeys);
                cmb.SetComputeBufferParam(_tileCs, _kernelTileAssign, TileProps.TileValues,      cache.tileValues);
                int tileAssignGroups = (splatCount + 1023) / 1024;
                cmb.DispatchCompute(_tileCs, _kernelTileAssign, tileAssignGroups, 1, 1);
                cmb.EndSample(ProfTileAssign);

                // ── Async readback of pair count (for next frame's sort budget) ──
                // Use cmb.RequestAsyncReadback so the readback is sequenced in the
                // command buffer — it reads the counter AFTER this camera's assign
                // kernel, not at an indeterminate time like the standalone API.
                if (!cache.asyncReadbackPending.Contains(camId))
                {
                    cache.asyncReadbackPending.Add(camId);
                    int capturedCamId = camId;
                    cmb.RequestAsyncReadback(cache.tilePairCounter, 4, 0, (AsyncGPUReadbackRequest req) =>
                    {
                        cache.asyncReadbackPending.Remove(capturedCamId);
                        if (!req.hasError && req.done)
                        {
                            var data = req.GetData<uint>();
                            if (data.Length > 0)
                            {
                                cache.asyncTilePairCounts[capturedCamId] = data[0];
                                if (data[0] >= (uint)GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs)
                                    Debug.LogWarning($"[GaussianSplat] Tile pair buffer overflow! pairs={data[0]:N0}, max={GaussianSplatRenderSystem.GlobalOrderGroupCache.MaxTilePairs:N0}. Some splats will be missing.");
                            }
                        }
                    });
                }

                // ── Sort tile pairs ───────────────────────────────────────
                cmb.BeginSample(ProfTileSort);
                cache.tileSorterArgs.inputKeys   = cache.tileKeys;
                cache.tileSorterArgs.inputValues = cache.tileValues;
                cache.tileSorterArgs.count       = sortCount;
                _tileSorter.Dispatch(cmb, cache.tileSorterArgs);
                cmb.EndSample(ProfTileSort);

                // ── Kernel 2: build tile ranges from sorted keys ──────────
                cmb.BeginSample(ProfTileBuild);
                cmb.SetComputeIntParam   (_tileCs, TileProps.TilePairCount, (int)sortCount);
                cmb.SetComputeIntParam   (_tileCs, TileProps.NumTiles,      numTiles);
                cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountX,    tileX);
                cmb.SetComputeBufferParam(_tileCs, _kernelBuildRanges, TileProps.SortedTileKeys, cache.tileKeys);
                cmb.SetComputeBufferParam(_tileCs, _kernelBuildRanges, TileProps.TileRanges,     cache.tileRanges);
                int buildGroups = ((int)sortCount + 1023) / 1024;
                cmb.DispatchCompute(_tileCs, _kernelBuildRanges, buildGroups, 1, 1);
                cmb.EndSample(ProfTileBuild);
                cache.hasTileRanges = true;
            }

            // ── Kernel 3: tile-based front-to-back alpha composite ────
            cmb.BeginSample(ProfTileRender);
            cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountX, tileX);
            cmb.SetComputeIntParam   (_tileCs, TileProps.TileCountY, tileY);
            cmb.SetComputeVectorParam(_tileCs, TileProps.ScreenParams,
                new Vector4(screenW, screenH, 1f / screenW, 1f / screenH));
            cmb.SetComputeBufferParam(_tileCs, _kernelRenderTiles, TileProps.SplatViewData,    cache.viewData);
            cmb.SetComputeBufferParam(_tileCs, _kernelRenderTiles, TileProps.SortedTileValues, cache.tileValues);
            cmb.SetComputeBufferParam(_tileCs, _kernelRenderTiles, TileProps.TileRangesRead,   cache.tileRanges);
            cmb.SetComputeTextureParam(_tileCs, _kernelRenderTiles, TileProps.OutputTexture, TileOutputTarget);
            cmb.DispatchCompute(_tileCs, _kernelRenderTiles, tileX, tileY, 1);
            cmb.EndSample(ProfTileRender);
        }

        public uint GetLastTilePairCount(
            Dictionary<int, GaussianSplatRenderSystem.GlobalOrderGroupCache> groups)
        {
            foreach (var kvp in groups)
            {
                uint max = 0;
                foreach (var c in kvp.Value.asyncTilePairCounts.Values)
                    if (c > max) max = c;
                return max;
            }
            return 0;
        }

        // ── Private methods ──────────────────────────────────────────────

        // Compute the optimal bit-split for tile sort keys:
        // tileIdBits = ceil(log2(numTiles)), depthBits = 32 - tileIdBits.
        // Maximises depth precision for the actual screen resolution.
        private static int CalcTileShift(int numTiles)
        {
            if (numTiles <= 1) return 28;
            // Integer ceil(log2): find smallest k such that (1 << k) >= numTiles
            int tileIdBits = 0;
            int v = numTiles - 1;
            while (v > 0) { v >>= 1; tileIdBits++; }
            int depthBits = 32 - tileIdBits;
            return Mathf.Clamp(depthBits, 8, 28);
        }
    }
}
