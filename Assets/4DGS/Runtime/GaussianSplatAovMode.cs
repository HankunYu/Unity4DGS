namespace GaussianSplatting.Runtime
{
    // Auxiliary output channels for the PointCloud render mode.
    //
    // An AOV replaces the beauty image for the whole run rather than rendering
    // alongside it: the values are not blendable, so they need their own blend
    // state and their own composite. Rendering is deterministic under
    // Time.captureFramerate, so a beauty pass and an AOV pass captured as
    // separate runs line up frame for frame.
    public enum GaussianSplatAovMode
    {
        None,

        // Distance from the eye to the splat centre, in metres, as a linear
        // float. Background is 0; alpha carries the coverage matte that tells
        // background apart from a genuine zero distance.
        //
        // Under OmniDirectionalStereo this is radial distance, not planar depth
        // — the only definition that means anything on an equirect, but tools
        // that assume a pinhole camera will misread it.
        Depth,
    }
}
