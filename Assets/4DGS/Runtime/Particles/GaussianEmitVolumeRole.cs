namespace GaussianSplatting.Runtime
{
    // What a GaussianEmitVolume does to its emitter's particles.
    // Values match the role in GaussianParticles.compute.
    public enum GaussianEmitVolumeRole
    {
        // Particles are born only on source splats inside birth volumes
        // (anywhere when the emitter has none), more rarely towards the edge
        Birth = 0,
        // Attached particles leave the surface when a release volume reaches
        // their splat, before their attach time is over
        Release = 1,
    }
}
