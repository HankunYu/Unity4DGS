namespace GaussianSplatting.Runtime
{
    // What picks the position along a style's color ramp.
    public enum GaussianSplatStyleRampInput
    {
        // Luminance of the splat's own color: keeps the image's light and dark
        // structure, remapped onto the ramp.
        Luminance,

        // Distance from the camera across the style's depth range.
        Depth,
    }
}
