namespace GaussianSplatting.Runtime
{
    // What picks the position along a style's color ramp (GaussianStyleColorBlock).
    // Values match STYLE_RAMP_* in GaussianStyleColor.hlsl.
    public enum GaussianSplatStyleRampInput
    {
        // Luminance of the splat's own color: keeps the image's light and dark
        // structure, remapped onto the ramp.
        Luminance = 0,

        // Distance from the camera across the style's ramp range.
        Depth = 1,

        // Position along the style's ramp axis, a world direction, across the
        // ramp range: the colors stay where they are in the scene and splats
        // move through them.
        WorldAxis = 2,

        // Distance from the centre of the style volume that applies the
        // style, across the ramp range: move the volume, or have it follow
        // something, to carry the colors along.
        VolumeDistance = 3,

        // The splat's baked occlusion (a .meshsplat with Bake Occlusion):
        // covered creases at the start of the ramp, open surfaces at its end.
        // The ramp range is not used.
        Occlusion = 4,

        // The style's depth mask (its Depth block): far from the camera at the
        // start of the ramp, near at its end. The ramp range is not used.
        VolumeDepth = 5,
    }
}
