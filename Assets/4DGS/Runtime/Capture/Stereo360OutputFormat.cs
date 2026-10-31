namespace GaussianSplatting.Runtime
{
    // Image format for the captured sequence.
    public enum Stereo360OutputFormat
    {
        // 8 bits per channel. Correct for a beauty pass, useless for an AOV:
        // a depth channel quantised to 256 steps carries no usable range.
        Png,

        // Half float per channel, zip compressed. Required for any AOV, and the
        // reason the capture target has to be a float render texture — frames
        // are roughly four times the size of the PNG equivalent.
        Exr,
    }
}
