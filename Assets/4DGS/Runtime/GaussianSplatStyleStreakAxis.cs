namespace GaussianSplatting.Runtime
{
    // Which way a streak trails from its splat. Streaks are world-space
    // segments, so both eyes of a stereo pair see the same line in depth.
    public enum GaussianSplatStyleStreakAxis
    {
        // A fixed world direction, such as down for falling light.
        WorldAxis,

        // Away from the camera along the view ray: on screen the streaks
        // radiate from the centre of view.
        ViewRay,
    }
}
