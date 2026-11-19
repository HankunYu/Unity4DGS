namespace GaussianSplatting.Runtime
{
    // How a style snaps splat positions (GaussianStyleQuantizeBlock).
    // Snapping is in world space, so both eyes of a stereo pair agree; the
    // style weight slides splats onto it.
    // Values match STYLE_QUANTIZE_* in GaussianStyleQuantize.hlsl.
    public enum GaussianSplatStyleQuantize
    {
        // Onto evenly spaced parallel planes, such as horizontal scan lines.
        Slices = 1,

        // Onto the points of a cubic grid, for a dot-matrix look.
        Grid = 2,
    }
}
