namespace GaussianSplatting.Runtime
{
    // What a stylized splat is drawn as. The style weight morphs the render
    // mode's own shape into this one.
    public enum GaussianSplatStyleShape
    {
        // The render mode's own shape: the Gaussian ellipse in Splats mode,
        // the point in PointCloud mode.
        Base,

        // A round dot of fixed screen size.
        Disc,

        // A thin line trailing from the splat, brightest at the splat.
        Streak,
    }
}
