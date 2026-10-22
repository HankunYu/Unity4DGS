namespace GaussianSplatting.Editor.Import
{
    // Data format presets; size/PSNR figures are relative to a float PLY.
    public enum GaussianSplatQuality
    {
        VeryHigh,   // 1.05x smaller
        High,       // 2.94x smaller, 57.77 PSNR
        Medium,     // 5.14x smaller, 47.46 PSNR
        Low,        // 14.01x smaller, 35.17 PSNR
        VeryLow,    // 18.62x smaller, 32.27 PSNR
        Custom,
    }
}
