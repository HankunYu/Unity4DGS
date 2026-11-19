namespace GaussianSplatting.Runtime
{
    // What a style's depth mask measures a splat's depth against
    // (GaussianStyleDepthBlock).
    // Values match STYLE_DEPTH_* in GaussianStyleDepth.hlsl.
    public enum GaussianSplatStyleDepthReference
    {
        // The centre of the style volume the splat is in: one near and far
        // side for all the volume holds
        VolumeCentre = 0,
        // The centre of the mesh the splat sits on, as GaussianMeshDriver
        // poses it: every mesh of a mesh splat (each figure, each prop) gets
        // its own near and far side, however many share a volume. The volume
        // centre stands in without a mesh driver.
        Mesh = 1,
    }
}
