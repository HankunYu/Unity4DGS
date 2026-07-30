namespace GaussianSplatting.Runtime
{
    // How Stereo360Capture builds each eye's equirect image.
    public enum Stereo360Projection
    {
        // Six cube faces per eye, each rendered with the camera displaced along
        // that face's horizontal right axis. Cheap and works for any geometry,
        // but the offset direction jumps at every face boundary, so the eyes
        // disagree along the 12 cube edges and at the 8 corners.
        Cubemap,

        // One render per eye with the eye position riding a circle that rotates
        // with the view azimuth. Geometrically correct everywhere, 2 renders per
        // frame instead of 12 — but it only projects splats, so other geometry
        // on the same camera still uses a normal frustum.
        OmniDirectionalStereo,
    }
}
