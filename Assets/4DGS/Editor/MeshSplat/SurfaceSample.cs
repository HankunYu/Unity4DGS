using Unity.Mathematics;

namespace GaussianSplatting.Editor.MeshSplat
{
    // A point bound to a mesh surface: which mesh, which triangle, and where
    // in it. Re-evaluating it on the deformed mesh gives the same surface
    // spot in every pose.
    public struct SurfaceSample
    {
        public int mesh;
        public int triangle;
        // Weights of the triangle's three corners, summing to 1
        public float3 bary;
        // Typical distance to the neighbouring samples here, in model space;
        // splats are sized from it
        public float spacing;
    }
}
