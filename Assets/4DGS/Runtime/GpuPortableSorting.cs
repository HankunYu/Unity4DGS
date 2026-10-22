using System;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Stable LSD radix sort without wave intrinsics (SplatPortableSort.compute).
    // Slower than GpuSorting's reduce-then-scan, but deterministic on every GPU
    // with compute shaders — the fallback where the wave-op kernels are
    // unsupported, such as visionOS Metal.
    public class GpuPortableSorting : IGpuSorter
    {
        private const uint Radix = 256;
        private const uint RadixBits = 8;
        private const uint KeysPerTile = 1024;    // THREADS * ITEMS in the shader

        // Two scan levels cover TILE * TILE histogram entries.
        public const uint MaxKeys = KeysPerTile * KeysPerTile / Radix * KeysPerTile;

        private static readonly int PropCount = Shader.PropertyToID("_Count");
        private static readonly int PropShift = Shader.PropertyToID("_Shift");
        private static readonly int PropTileCount = Shader.PropertyToID("_TileCount");
        private static readonly int PropScanCount = Shader.PropertyToID("_ScanCount");
        private static readonly int PropSumCount = Shader.PropertyToID("_SumCount");
        private static readonly int PropKeysIn = Shader.PropertyToID("_KeysIn");
        private static readonly int PropValuesIn = Shader.PropertyToID("_ValuesIn");
        private static readonly int PropKeysOut = Shader.PropertyToID("_KeysOut");
        private static readonly int PropValuesOut = Shader.PropertyToID("_ValuesOut");
        private static readonly int PropHistogram = Shader.PropertyToID("_Histogram");
        private static readonly int PropScanSums = Shader.PropertyToID("_ScanSums");

        private readonly ComputeShader _cs;
        private readonly int _kernelUpsweep = -1;
        private readonly int _kernelScanTiles = -1;
        private readonly int _kernelScanSums = -1;
        private readonly int _kernelScanAdd = -1;
        private readonly int _kernelDownsweep = -1;
        private readonly bool _valid;

        public bool Valid => _valid;

        public GpuPortableSorting(ComputeShader cs)
        {
            _cs = cs;
            if (cs == null)
                return;
            try
            {
                _kernelUpsweep = cs.FindKernel("CSUpsweep");
                _kernelScanTiles = cs.FindKernel("CSScanTiles");
                _kernelScanSums = cs.FindKernel("CSScanSums");
                _kernelScanAdd = cs.FindKernel("CSScanAdd");
                _kernelDownsweep = cs.FindKernel("CSDownsweep");
            }
            catch (ArgumentException)
            {
                return;
            }
            _valid = cs.IsSupported(_kernelUpsweep) &&
                     cs.IsSupported(_kernelScanTiles) &&
                     cs.IsSupported(_kernelScanSums) &&
                     cs.IsSupported(_kernelScanAdd) &&
                     cs.IsSupported(_kernelDownsweep);
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        public void Dispatch(CommandBuffer cmd, GpuSortArgs args)
        {
            if (!_valid || args.count == 0)
                return;
            Assert.IsTrue(args.sortStartBit % 16 == 0 && args.sortStartBit < 32);
            if (args.count > MaxKeys)
            {
                Debug.LogError($"[GpuPortableSorting] {args.count} keys exceed the {MaxKeys} supported; not sorted.");
                return;
            }

            uint tileCount = DivRoundUp(args.count, KeysPerTile);
            uint scanCount = Radix * tileCount;
            uint sumCount = DivRoundUp(scanCount, KeysPerTile);

            cmd.SetComputeIntParam(_cs, PropCount, (int)args.count);
            cmd.SetComputeIntParam(_cs, PropTileCount, (int)tileCount);
            cmd.SetComputeIntParam(_cs, PropScanCount, (int)scanCount);
            cmd.SetComputeIntParam(_cs, PropSumCount, (int)sumCount);

            cmd.SetComputeBufferParam(_cs, _kernelUpsweep, PropHistogram, args.resources.histogram);
            cmd.SetComputeBufferParam(_cs, _kernelScanTiles, PropHistogram, args.resources.histogram);
            cmd.SetComputeBufferParam(_cs, _kernelScanTiles, PropScanSums, args.resources.scratch);
            cmd.SetComputeBufferParam(_cs, _kernelScanSums, PropScanSums, args.resources.scratch);
            cmd.SetComputeBufferParam(_cs, _kernelScanAdd, PropHistogram, args.resources.histogram);
            cmd.SetComputeBufferParam(_cs, _kernelScanAdd, PropScanSums, args.resources.scratch);
            cmd.SetComputeBufferParam(_cs, _kernelDownsweep, PropHistogram, args.resources.histogram);

            GraphicsBuffer srcKeys = args.inputKeys;
            GraphicsBuffer srcValues = args.inputValues;
            GraphicsBuffer dstKeys = args.resources.altKeys;
            GraphicsBuffer dstValues = args.resources.altValues;

            for (uint shift = args.sortStartBit; shift < 32; shift += RadixBits)
            {
                cmd.SetComputeIntParam(_cs, PropShift, (int)shift);

                cmd.SetComputeBufferParam(_cs, _kernelUpsweep, PropKeysIn, srcKeys);
                cmd.DispatchCompute(_cs, _kernelUpsweep, (int)tileCount, 1, 1);

                cmd.DispatchCompute(_cs, _kernelScanTiles, (int)sumCount, 1, 1);
                cmd.DispatchCompute(_cs, _kernelScanSums, 1, 1, 1);
                cmd.DispatchCompute(_cs, _kernelScanAdd, (int)sumCount, 1, 1);

                cmd.SetComputeBufferParam(_cs, _kernelDownsweep, PropKeysIn, srcKeys);
                cmd.SetComputeBufferParam(_cs, _kernelDownsweep, PropValuesIn, srcValues);
                cmd.SetComputeBufferParam(_cs, _kernelDownsweep, PropKeysOut, dstKeys);
                cmd.SetComputeBufferParam(_cs, _kernelDownsweep, PropValuesOut, dstValues);
                cmd.DispatchCompute(_cs, _kernelDownsweep, (int)tileCount, 1, 1);

                (srcKeys, dstKeys) = (dstKeys, srcKeys);
                (srcValues, dstValues) = (dstValues, srcValues);
            }
        }
    }
}
