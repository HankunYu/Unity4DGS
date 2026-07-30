namespace GaussianSplatting.Runtime
{
    // How splat centres are projected to clip space.
    //
    // A fixed camera pair cannot render stereo 360: every azimuth needs its own
    // baseline, perpendicular to that azimuth's view direction. OmniDirectional
    // Stereo solves this by letting each eye ride a circle of radius IPD/2, so
    // the eye position rotates with the view azimuth and each column of the
    // equirect image comes from a different camera position.
    //
    // Rasterising that is normally impossible — one draw call has one camera
    // position — but splats are point primitives, small enough that a per-splat
    // non-linear projection introduces no visible interpolation error. So the
    // whole sphere can be projected analytically in a single pass, with no
    // cubemap faces and no slit stitching.
    public enum GaussianSplatProjectionMode
    {
        Perspective,
        OmniDirectionalStereo,
    }
}
