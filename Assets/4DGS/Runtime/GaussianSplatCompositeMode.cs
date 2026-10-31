namespace GaussianSplatting.Runtime
{
    // How splats reach the camera target.
    public enum GaussianSplatCompositeMode
    {
        // Front-to-back into a half-float intermediate RT, then one composite
        // pass over the scene. Required by the tile renderer, the Gaussian-only
        // stylize effect, AOVs and ODS. Blends in linear space, like Direct,
        // so both modes give the same image.
        Offscreen,

        // Back-to-front straight into the camera target, depth-tested against
        // scene geometry. Skips the intermediate RT and the composite — a full
        // screen write and read per eye, which tile-based mobile GPUs pay for
        // in bandwidth — and rasterises under the camera's foveation rate map.
        // Falls back to Offscreen wherever that path is required.
        Direct,
    }
}
