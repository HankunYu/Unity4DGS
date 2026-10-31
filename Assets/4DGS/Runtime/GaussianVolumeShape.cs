namespace GaussianSplatting.Runtime
{
    // Shape of a GaussianVolume: the unit cube or the unit-diameter sphere,
    // sized and placed by the volume's transform.
    // Values match GaussianVolumeWeight in GaussianVolume.hlsl.
    public enum GaussianVolumeShape
    {
        Box = 0,
        Sphere = 1,
    }
}
