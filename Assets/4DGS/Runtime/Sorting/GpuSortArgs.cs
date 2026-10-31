using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // One sort of (uint key, uint payload) pairs, ascending by key. Both
    // buffers hold the result afterwards: every sorter runs an even number of
    // passes, ping-ponging through the scratch resources.
    public struct GpuSortArgs
    {
        public uint count;
        public GraphicsBuffer inputKeys;
        public GraphicsBuffer inputValues;
        public GpuSortResources resources;
        // First key bit to sort from, must be a multiple of 16 so the even
        // pass count leaves results in the input buffers. 0 = full 32-bit
        // sort. 16 = top 16 bits only (2x faster) — valid ONLY for keys
        // whose low 16 bits are insignificant (e.g. distance buckets);
        // packed keys like tile|depth or Morton codes need a full sort.
        public uint sortStartBit;
    }
}
