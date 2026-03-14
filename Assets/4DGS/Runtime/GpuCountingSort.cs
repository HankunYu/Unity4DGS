using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    public class GpuCountingSort
    {
        private const int NumBuckets = 4096;

        private readonly ComputeShader _cs;
        private readonly int _kernelClear = -1;
        private readonly int _kernelCount = -1;
        private readonly int _kernelPrefixSum = -1;
        private readonly int _kernelScatter = -1;

        private readonly bool _valid;

        private GraphicsBuffer _histogram;

        public bool Valid => _valid;

        public GpuCountingSort(ComputeShader cs)
        {
            _cs = cs;
            if (cs)
            {
                try
                {
                    _kernelClear = cs.FindKernel("CSCountingSortClear");
                    _kernelCount = cs.FindKernel("CSCountingSortCount");
                    _kernelPrefixSum = cs.FindKernel("CSCountingSortPrefixSum");
                    _kernelScatter = cs.FindKernel("CSCountingSortScatter");
                }
                catch (Exception)
                {
                    _kernelClear = -1;
                    _kernelCount = -1;
                    _kernelPrefixSum = -1;
                    _kernelScatter = -1;
                }
            }

            _valid = _kernelClear >= 0 &&
                     _kernelCount >= 0 &&
                     _kernelPrefixSum >= 0 &&
                     _kernelScatter >= 0;

            if (_valid)
            {
                if (!cs.IsSupported(_kernelClear) ||
                    !cs.IsSupported(_kernelCount) ||
                    !cs.IsSupported(_kernelPrefixSum) ||
                    !cs.IsSupported(_kernelScatter))
                {
                    _valid = false;
                }
            }
        }

        private void EnsureResources()
        {
            if (_histogram == null || !_histogram.IsValid())
            {
                _histogram?.Dispose();
                _histogram = new GraphicsBuffer(GraphicsBuffer.Target.Structured, NumBuckets, 4)
                {
                    name = "CountingSortHistogram"
                };
            }
        }

        public void Dispatch(CommandBuffer cmd, uint count,
            GraphicsBuffer inputDistances, GraphicsBuffer outputKeys)
        {
            if (!_valid)
                return;

            EnsureResources();

            cmd.SetComputeIntParam(_cs, "_SplatCount", (int)count);

            // Clear histogram
            cmd.SetComputeBufferParam(_cs, _kernelClear, "_Histogram", _histogram);
            _cs.GetKernelThreadGroupSizes(_kernelClear, out uint clearThreads, out _, out _);
            int clearGroups = (int)((NumBuckets + clearThreads - 1) / clearThreads);
            cmd.DispatchCompute(_cs, _kernelClear, clearGroups, 1, 1);

            // Count
            cmd.SetComputeBufferParam(_cs, _kernelCount, "_Histogram", _histogram);
            cmd.SetComputeBufferParam(_cs, _kernelCount, "_SortInput", inputDistances);
            _cs.GetKernelThreadGroupSizes(_kernelCount, out uint countThreads, out _, out _);
            int countGroups = (int)((count + countThreads - 1) / countThreads);
            cmd.DispatchCompute(_cs, _kernelCount, countGroups, 1, 1);

            // Prefix sum — single group
            cmd.SetComputeBufferParam(_cs, _kernelPrefixSum, "_Histogram", _histogram);
            cmd.DispatchCompute(_cs, _kernelPrefixSum, 1, 1, 1);

            // Scatter
            cmd.SetComputeBufferParam(_cs, _kernelScatter, "_Histogram", _histogram);
            cmd.SetComputeBufferParam(_cs, _kernelScatter, "_SortInput", inputDistances);
            cmd.SetComputeBufferParam(_cs, _kernelScatter, "_SortOutput", outputKeys);
            _cs.GetKernelThreadGroupSizes(_kernelScatter, out uint scatterThreads, out _, out _);
            int scatterGroups = (int)((count + scatterThreads - 1) / scatterThreads);
            cmd.DispatchCompute(_cs, _kernelScatter, scatterGroups, 1, 1);
        }

        public void Dispose()
        {
            _histogram?.Dispose();
            _histogram = null;
        }
    }
}
