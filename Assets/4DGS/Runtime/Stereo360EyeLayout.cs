namespace GaussianSplatting.Runtime
{
    // How the two eyes are packed into one output frame.
    public enum Stereo360EyeLayout
    {
        // Left eye on top. The convention for stereo equirect, because it keeps
        // full horizontal resolution per eye — the scarce axis in a panorama.
        OverUnder,

        // Left eye on the left. Halves horizontal resolution per eye; only
        // useful when a downstream tool demands this layout.
        SideBySide,

        // One render instead of two. Not a deliverable format — it exists so
        // framing and settings can be checked at half the cost.
        LeftEyeOnly,
    }
}
