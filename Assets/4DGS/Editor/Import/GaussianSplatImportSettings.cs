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

        // Caps the splat count, keeping the most important splats (opacity
        // times projected size). Per-frame sorting and view-data cost grow
        // with the count, so this is the most direct performance setting.
        public GaussianSplatBudgetMode budgetMode = GaussianSplatBudgetMode.None;
        [Min(1)] public int budgetCount = 1_000_000;
        [Range(1f, 100f)] public float budgetPercent = 100f;

        public bool importCameras = true;

        // SH bands above this are dropped. Each band adds view-dependent
        // color but also data that the view-data pass reads every frame.
        [Range(0, GaussianSplatAsset.MaxSHDegree)] public int maxSHDegree = GaussianSplatAsset.MaxSHDegree;

        public GaussianSplatFormats Formats =>
            quality == GaussianSplatQuality.Custom ? customFormats : GaussianSplatFormats.ForQuality(quality);

        // The SH degree an asset built from a source with sourceDegree gets
        public int SHDegreeFor(int sourceDegree) => Math.Min(sourceDegree, maxSHDegree);

        // The most splats an asset built from a source with sourceCount
        // splats keeps; pruning by opacity may leave fewer
        public int SplatBudgetFor(int sourceCount)
        {
            return budgetMode switch
            {
                GaussianSplatBudgetMode.Count => Math.Min(sourceCount, Math.Max(1, budgetCount)),
                GaussianSplatBudgetMode.Percentage => Math.Clamp((int)Math.Ceiling(sourceCount * (double)budgetPercent / 100.0), 1, sourceCount),
                _ => sourceCount,
            };
        }
    }
}
