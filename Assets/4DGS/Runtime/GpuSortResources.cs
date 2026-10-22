using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Scratch buffers for one sort of up to `count` keys, sized for every
    // IGpuSorter implementation so callers never care which one runs.
    public struct GpuSortResources
    {
        // Wave radix scratch: 4 passes x 256 global bins.
        private const uint WaveScratchSize = 1024;

        public GraphicsBuffer altKeys;
        public GraphicsBuffer altValues;
        public GraphicsBuffer histogram;
        public GraphicsBuffer scratch;

        public static GpuSortResources Load(uint count)
        {
            // The portable sorter's 1024-key tiles need more histogram entries
            // than the wave sorter's larger partitions.
            uint histogramSize = GpuPortableSorting.HistogramSize(count);
            uint scratchSize = System.Math.Max(WaveScratchSize, GpuPortableSorting.BlockSumsSize(count));
            var target = GraphicsBuffer.Target.Structured;
            return new GpuSortResources
            {
                altKeys = new GraphicsBuffer(target, (int)count, 4) { name = "GpuSortAltKeys" },
                altValues = new GraphicsBuffer(target, (int)count, 4) { name = "GpuSortAltValues" },
                histogram = new GraphicsBuffer(target, (int)histogramSize, 4) { name = "GpuSortHistogram" },
                scratch = new GraphicsBuffer(target, (int)scratchSize, 4) { name = "GpuSortScratch" },
            };
        }

        // Deferred like every other splat buffer: sort dispatches recorded
        // this frame may not have been translated by the render thread yet.
        public void Dispose()
        {
            GaussianSplatRenderSystem.DeferDispose(altKeys);
            GaussianSplatRenderSystem.DeferDispose(altValues);
            GaussianSplatRenderSystem.DeferDispose(histogram);
            GaussianSplatRenderSystem.DeferDispose(scratch);

            altKeys = null;
            altValues = null;
            histogram = null;
            scratch = null;
        }
    }
}
