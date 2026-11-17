namespace GaussianSplatting.Runtime
{
    // Which lights a GaussianSplatLighting takes
    public enum GaussianSplatLightSource
    {
        // Every active light in the scene whose culling mask takes the
        // renderer's layer, the way a mesh on that layer is lit
        Scene,
        // Only the lights listed on the component
        List,
    }
}
