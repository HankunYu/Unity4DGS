namespace GaussianSplatting.Runtime
{
    // How splats reach the camera target.
    public enum GaussianSplatCompositeMode
    {
        // Front-to-back into a half-float intermediate RT, then one composite
        // pass over the scene. Blends in gamma space like the training
        // renderer, and is required by the tile renderer, the Gaussian-only
        // stylize effect and AOVs.
        Offscreen,

        // Back-to-front straight into the camera target, depth-tested against
        // scene geometry. Skips the intermediate RT and the composite — a full
        // screen write and read per eye, which tile-based mobile GPUs pay for
        // in bandwidth — and rasterises under the camera's foveation rate map.
        // Blends in linear space, so soft edges look slightly different.
        // Falls back to Offscreen wherever that path is required.
        Direct,
    }
}
