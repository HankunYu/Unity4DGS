using System.Collections.Generic;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Decides where on a model's surface the splats go. Samplers see every
    // mesh at once, so they can account for meshes covering each other.
    // The result must only depend on the meshes and the sampler's settings:
    // a re-import has to reproduce the same points.
    public interface ISurfaceSampler
    {
        List<SurfaceSample> Sample(IReadOnlyList<SurfaceMesh> meshes);
    }
}
