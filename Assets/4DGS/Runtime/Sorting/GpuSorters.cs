using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Picks the fastest sorter the running GPU supports.
    public static class GpuSorters
    {
        private static bool _fallbackLogged;

        // The wave-intrinsic radix sort when supported, else the portable one;
        // null only when neither compute shader is usable.
        public static IGpuSorter Create(ComputeShader waveSort, ComputeShader portableSort)
        {
            if (waveSort != null)
            {
                var sorter = new GpuSorting(waveSort);
                if (sorter.Valid)
                    return sorter;
            }
            if (portableSort != null)
            {
                var sorter = new GpuPortableSorting(portableSort);
                if (sorter.Valid)
                {
                    LogFallbackOnce();
                    return sorter;
                }
            }
            return null;
        }

        private static void LogFallbackOnce()
        {
            if (_fallbackLogged)
                return;
            _fallbackLogged = true;
            Debug.Log("GPU radix sort with wave intrinsics is unsupported on this platform; " +
                      "using the portable radix sort.");
        }
    }
}
