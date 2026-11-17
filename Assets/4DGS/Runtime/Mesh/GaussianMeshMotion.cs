namespace GaussianSplatting.Runtime
{
    // What moves a bound mesh's surface, and so its splats.
    public enum GaussianMeshMotion
    {
        // The renderer's transform alone
        Rigid = 0,
        // The renderer's bones
        Skinned = 1,
        // The mesh's own vertices, rewritten as it plays (a vertex cache
        // such as Alembic), under the renderer's transform
        Vertices = 2,
    }
}
