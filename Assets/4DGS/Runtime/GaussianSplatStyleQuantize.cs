namespace GaussianSplatting.Runtime
{
    // How a style snaps splat positions. Snapping is in world space, so both
    // eyes of a stereo pair agree; the style weight slides splats onto it.
    public enum GaussianSplatStyleQuantize
    {
        // Splats stay where they are.
        None,

        // Onto evenly spaced parallel planes, such as horizontal scan lines.
        Slices,

        // Onto the points of a cubic grid, for a dot-matrix look.
        Grid,
    }
}
