namespace GaussianSplatting.Runtime
{
    // What a stylized splat is drawn as (GaussianStyleShapeBlock). The style
    // weight morphs the render mode's own shape into this one; a style
    // without the block keeps the render mode's shape, 0 to the shaders.
    // Values match STYLE_SHAPE_* in GaussianStyleShape.hlsl.
    public enum GaussianSplatStyleShape
    {
        // A round dot of fixed screen size.
        Disc = 1,

        // A thin line trailing from the splat, brightest at the splat.
        Streak = 2,

        // A solid ball of fixed world size, shaded by the key light.
        Sphere = 3,

        // A solid cube of fixed world size along the world axes, shaded by
        // the key light.
        Cube = 4,
    }
}
