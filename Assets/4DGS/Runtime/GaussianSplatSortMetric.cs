namespace GaussianSplatting.Runtime
{
    // What splats are ordered by before blending.
    public enum GaussianSplatSortMetric
    {
        // Distance from the camera position. The order only changes when the
        // camera moves, not when it turns, so a sort that runs every few
        // frames (or lags a frame behind) stays correct under head rotation —
        // the property a VR renderer needs to throttle sorting.
        Radial,

        // Depth along the camera's view axis. Exact for a pinhole camera at
        // the screen centre, but every rotation invalidates the order.
        ViewDepth,
    }
}
