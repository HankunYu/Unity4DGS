using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // A GPU sort of (uint key, uint payload) pairs. Implementations differ in
    // what hardware they need, not in what they produce: ascending, stable
    // for the bits sorted, result in the input buffers.
    public interface IGpuSorter
    {
        // False when the kernels are unsupported on the running device.
        bool Valid { get; }

        void Dispatch(CommandBuffer cmd, GpuSortArgs args);
    }
}
