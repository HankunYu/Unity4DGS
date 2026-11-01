using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Picks the fastest sorter the running GPU supports.
    public static class GpuSorters
    {
        private static string _lastChoiceLog;

        // The wave-intrinsic radix sort when supported, else the portable one;
        // null only when neither compute shader is usable.
        public static IGpuSorter Create(ComputeShader waveSort, ComputeShader portableSort)
        {
            string waveReason = "SplatSort compute shader is missing";
            if (waveSort != null)
            {
                var sorter = new GpuSorting(waveSort);
                if (sorter.Valid)
                {
                    LogChoice($"wave-intrinsic radix sort (GPU subgroup size {SystemInfo.computeSubGroupSize})");
                    return sorter;
                }
                waveReason = sorter.UnsupportedReason;
            }
            if (portableSort != null)
            {
                var sorter = new GpuPortableSorting(portableSort);
                if (sorter.Valid)
                {
                    LogChoice($"portable radix sort; wave sort unavailable: {waveReason}");
                    return sorter;
                }
            }
            LogChoice($"no usable GPU sorter; wave sort unavailable: {waveReason}", true);
            return null;
        }

        // Several renderers create sorters, so a choice is logged only when it changes.
        private static void LogChoice(string choice, bool isError = false)
        {
            string message = $"Gaussian splat sorting: {choice}.";
            if (message == _lastChoiceLog)
                return;
            _lastChoiceLog = message;
            if (isError)
                Debug.LogError(message);
            else
                Debug.Log(message);
        }
    }
}
