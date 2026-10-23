using System;
using GaussianSplatting.Runtime;
using UnityEngine;

namespace GaussianSplatting.Editor.Import
{
    // Everything that turns a source file into a GaussianSplatAsset. Lossy
    // steps live here so they can be changed and re-run from the source.
    [Serializable]
    public class GaussianSplatImportSettings
    {
        public GaussianSplatQuality quality = GaussianSplatQuality.Medium;
        // Used only when quality is Custom
        public GaussianSplatFormats customFormats = GaussianSplatFormats.ForQuality(GaussianSplatQuality.Medium);

        // Splats below this opacity are dropped. Every splat costs sorting
        // and view-data work each frame even when it never shows; the
        // renderer culls below 5/255 at opacity scale 1. 0 keeps everything.
        [Range(0f, 0.1f)] public float pruneOpacity;

        public bool importCameras = true;

        // SH bands above this are dropped. Each band adds view-dependent
        // color but also data that the view-data pass reads every frame.
        [Range(0, GaussianSplatAsset.MaxSHDegree)] public int maxSHDegree = GaussianSplatAsset.MaxSHDegree;

        public GaussianSplatFormats Formats =>
            quality == GaussianSplatQuality.Custom ? customFormats : GaussianSplatFormats.ForQuality(quality);

        // The SH degree an asset built from a source with sourceDegree gets
        public int SHDegreeFor(int sourceDegree) => Math.Min(sourceDegree, maxSHDegree);
    }
}
