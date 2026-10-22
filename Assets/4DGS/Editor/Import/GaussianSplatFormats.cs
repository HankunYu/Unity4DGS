using System;
using GaussianSplatting.Runtime;

namespace GaussianSplatting.Editor.Import
{
    // The per-attribute encodings an asset is built with.
    [Serializable]
    public struct GaussianSplatFormats
    {
        public GaussianSplatAsset.VectorFormat pos;
        public GaussianSplatAsset.VectorFormat scale;
        public GaussianSplatAsset.ColorFormat color;
        public GaussianSplatAsset.SHFormat sh;

        // Anything lossy is quantized relative to per-chunk bounds; a fully
        // float asset keeps its data as-is and has no chunk table.
        public bool UsesChunks =>
            pos != GaussianSplatAsset.VectorFormat.Float32 ||
            scale != GaussianSplatAsset.VectorFormat.Float32 ||
            color != GaussianSplatAsset.ColorFormat.Float32x4 ||
            sh != GaussianSplatAsset.SHFormat.Float32;

        public bool UsesSHClusters => sh >= GaussianSplatAsset.SHFormat.Cluster64k;

        public static GaussianSplatFormats ForQuality(GaussianSplatQuality quality)
        {
            return quality switch
            {
                GaussianSplatQuality.VeryHigh => new GaussianSplatFormats
                {
                    pos = GaussianSplatAsset.VectorFormat.Float32,
                    scale = GaussianSplatAsset.VectorFormat.Float32,
                    color = GaussianSplatAsset.ColorFormat.Float32x4,
                    sh = GaussianSplatAsset.SHFormat.Float32,
                },
                GaussianSplatQuality.High => new GaussianSplatFormats
                {
                    pos = GaussianSplatAsset.VectorFormat.Norm16,
                    scale = GaussianSplatAsset.VectorFormat.Norm16,
                    color = GaussianSplatAsset.ColorFormat.Float16x4,
                    sh = GaussianSplatAsset.SHFormat.Norm11,
                },
                GaussianSplatQuality.Medium => new GaussianSplatFormats
                {
                    pos = GaussianSplatAsset.VectorFormat.Norm11,
                    scale = GaussianSplatAsset.VectorFormat.Norm11,
                    color = GaussianSplatAsset.ColorFormat.Norm8x4,
                    sh = GaussianSplatAsset.SHFormat.Norm6,
                },
                GaussianSplatQuality.Low => new GaussianSplatFormats
                {
                    pos = GaussianSplatAsset.VectorFormat.Norm11,
                    scale = GaussianSplatAsset.VectorFormat.Norm6,
                    color = GaussianSplatAsset.ColorFormat.Norm8x4,
                    sh = GaussianSplatAsset.SHFormat.Cluster16k,
                },
                GaussianSplatQuality.VeryLow => new GaussianSplatFormats
                {
                    pos = GaussianSplatAsset.VectorFormat.Norm11,
                    scale = GaussianSplatAsset.VectorFormat.Norm6,
                    color = GaussianSplatAsset.ColorFormat.BC7,
                    sh = GaussianSplatAsset.SHFormat.Cluster4k,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(quality), quality, "Custom has no preset formats"),
            };
        }
    }
}
