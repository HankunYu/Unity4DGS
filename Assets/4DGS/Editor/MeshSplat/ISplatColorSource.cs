using System.Collections.Generic;
using Unity.Mathematics;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Gives every sample its base color, in the 0..1 display-referred space
    // 3DGS colors are stored in.
    public interface ISplatColorSource
    {
        void Fill(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples, float3[] colors);
    }
}
