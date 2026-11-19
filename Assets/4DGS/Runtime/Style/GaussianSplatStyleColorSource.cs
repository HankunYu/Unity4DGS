namespace GaussianSplatting.Runtime
{
    // Where a stylized splat takes its base color from.
    public enum GaussianSplatStyleColorSource
    {
        // The splat's own shaded (SH) color.
        Original = 0,

        // A color from the style's ramp, looked up by GaussianSplatStyleRampInput.
        Ramp = 1,
    }
}
