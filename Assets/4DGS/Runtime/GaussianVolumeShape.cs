namespace GaussianSplatting.Runtime
{
    // Shape of a GaussianVolume: the unit cube or the unit-diameter sphere,
    // sized and placed by the volume's transform, or everywhere.
    // Values match GaussianVolumeWeight in GaussianVolume.hlsl.
    public enum GaussianVolumeShape
    {
        Box = 0,
        Sphere = 1,
        // Full weight everywhere: the transform only places what the volume
        // drives (e.g. a swirl's centre), and the falloff does nothing.
        Unbounded = 2,
    }
}
