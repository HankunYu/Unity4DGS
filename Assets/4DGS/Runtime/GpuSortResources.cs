using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Scratch buffers for one sort of up to `count` keys, sized for every
    // IGpuSorter implementation so callers never care which one runs.
    public struct GpuSortResources
    {
        // Histogram sized for the smaller tile of the two sorters (the
        // portable one's 1024 keys), which needs the most entries.
        private const uint MinKeysPerTile = 1024;
        private const uint Radix = 256;
        // Wave radix: 4 passes x 256 global bins. Portable: one total per
        // scan group, at most one tile's worth.
        private const int ScratchSize = 1024;

        public GraphicsBuffer altKeys;
        public GraphicsBuffer altValues;
        public GraphicsBuffer histogram;
        public GraphicsBuffer scratch;

        public static GpuSortResources Load(uint count)
        {
            uint histogramSize = (count + MinKeysPerTile - 1) / MinKeysPerTile * Radix;
            var target = GraphicsBuffer.Target.Structured;
            return new GpuSortResources
            {
                altKeys = new GraphicsBuffer(target, (int)count, 4) { name = "GpuSortAltKeys" },
                altValues = new GraphicsBuffer(target, (int)count, 4) { name = "GpuSortAltValues" },
                histogram = new GraphicsBuffer(target, (int)histogramSize, 4) { name = "GpuSortHistogram" },
                scratch = new GraphicsBuffer(target, ScratchSize, 4) { name = "GpuSortScratch" },
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
