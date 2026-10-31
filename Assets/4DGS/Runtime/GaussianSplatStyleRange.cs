using System.Runtime.InteropServices;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // One renderer's slice of the splats a stylized draw covers, with the
    // style parameters the draw itself applies, per style slot
    // (GaussianStyleSet); the style color, the shape's quad and each splat's
    // style slot and weight are already in the view data. A global draw
    // merges several renderers into one sorted buffer, so the shader looks
    // each splat's renderer up by index.
    // Layout matches SplatStyleRange in RenderGaussianStylizedPass.hlsl.
    [StructLayout(LayoutKind.Sequential)]
    internal struct GaussianSplatStyleRange
    {
        public const int Stride = 64;

        // First splat of the renderer in the draw's view data; ranges ascend
        public uint start;
        // Non-zero when the view data holds baked quads (a styled renderer);
        // zero for a plain renderer sharing a global draw
        public uint baked;
        public uint pad0;
        public uint pad1;
        // Per slot: style additivity at full weight, which the shader scales
        // by each splat's style weight; GaussianSplatStyleShape; streak tail power
        public Vector4 additivity;
        public Vector4 shape;
        public Vector4 streakTailPower;
    }
}
