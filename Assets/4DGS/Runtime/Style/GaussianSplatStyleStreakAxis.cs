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
        // Behind the splat along its motion, as long as the path it covers
        // in the streak time: fast splats draw long lines. Needs a velocity
        // source on the renderer (GaussianParticleEmitter); without one, or
        // for still splats, the streaks shrink to dots.
        Velocity,
        // From the splat to the other end of its own segment, which the
        // renderer's segment source gives per splat: GaussianParticleLinks
        // (lines between nearby particles), or GaussianParticleEmitter (back
        // to the spot on the source each particle came from). Without one
        // the streaks shrink to dots.
        Segment,
        // Along the surface the splat lies on, across the streak direction:
        // with the direction up, the streaks run level around the form like
        // the hatching of an engraving. For mesh splats, whose rotation
        // carries the surface normal; where the surface faces the streak
        // direction the streaks shrink to dots.
        Surface,
    }
}
