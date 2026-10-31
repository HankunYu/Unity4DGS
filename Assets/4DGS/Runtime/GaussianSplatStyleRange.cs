using System.Runtime.InteropServices;

namespace GaussianSplatting.Runtime
{
    // One renderer's slice of the splats a stylized draw covers, with the
    // style parameters the draw itself applies; the style color, the shape's
    // quad and each splat's style weight are already in the view data. A
    // global draw merges several
    // renderers into one sorted buffer, so the shader looks each splat's
    // renderer up by index.
    // Layout matches SplatStyleRange in RenderGaussianStylizedPass.hlsl.
    [StructLayout(LayoutKind.Sequential)]
    internal struct GaussianSplatStyleRange
    {
        public const int Stride = 32;

        // First splat of the renderer in the draw's view data; ranges ascend
        public uint start;
        // Style additivity at full weight; the shader scales it by each
        // splat's style weight, which the view data carries
        public float additivity;
        // Non-zero when the view data holds baked quads (a styled renderer);
        // zero for a plain renderer sharing a global draw
        public uint baked;
        // GaussianSplatStyleShape
        public uint shape;
        public float streakTailPower;
        public float pad0;
        public float pad1;
        public float pad2;
    }
}
