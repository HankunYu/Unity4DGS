using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // GPU (uint key, uint payload) 8 bit-LSD radix sort, using reduce-then-scan
    // Copyright Thomas Smith 2024, MIT license
    // https://github.com/b0nes164/GPUSorting

    public class GpuSorting : IGpuSorter
    {
        //The size of a threadblock partition in the sort
        const uint DEVICE_RADIX_SORT_PARTITION_SIZE = 3840;

        //The size of our radix in bits
        const uint DEVICE_RADIX_SORT_BITS = 8;

        //Number of digits in our radix, 1 << DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_RADIX = 256;

        //Number of sorting passes required to sort a 32bit key, KEY_BITS / DEVICE_RADIX_SORT_BITS
        const uint DEVICE_RADIX_SORT_PASSES = 4;

        // Keywords to enable for the shader
        private LocalKeyword _keyUintKeyword;
        private LocalKeyword _payloadUintKeyword;
        private LocalKeyword _ascendKeyword;
        private LocalKeyword _sortPairKeyword;
        private LocalKeyword _vulkanKeyword;

        private readonly ComputeShader _cs;
        private readonly int _kernelInit = -1;
        private readonly int _kernelUpsweep = -1;
        private readonly int _kernelScan = -1;
        private readonly int _kernelDownsweep = -1;

        private readonly bool _valid;

        public bool Valid => _valid;

        public GpuSorting(ComputeShader cs)
        {
            _cs = cs;
            if (cs)
            {
                _kernelInit = cs.FindKernel("InitDeviceRadixSort");
                _kernelUpsweep = cs.FindKernel("Upsweep");
                _kernelScan = cs.FindKernel("Scan");
                _kernelDownsweep = cs.FindKernel("Downsweep");
            }

            _valid = _kernelInit >= 0 &&
                      _kernelUpsweep >= 0 &&
                      _kernelScan >= 0 &&
                      _kernelDownsweep >= 0;
            if (_valid)
            {
                if (!cs.IsSupported(_kernelInit) ||
                    !cs.IsSupported(_kernelUpsweep) ||
                    !cs.IsSupported(_kernelScan) ||
                    !cs.IsSupported(_kernelDownsweep))
                {
                    _valid = false;
                }
            }

            _keyUintKeyword = new LocalKeyword(cs, "KEY_UINT");
            _payloadUintKeyword = new LocalKeyword(cs, "PAYLOAD_UINT");
            _ascendKeyword = new LocalKeyword(cs, "SHOULD_ASCEND");
            _sortPairKeyword = new LocalKeyword(cs, "SORT_PAIRS");
            _vulkanKeyword = new LocalKeyword(cs, "VULKAN");

            cs.EnableKeyword(_keyUintKeyword);
            cs.EnableKeyword(_payloadUintKeyword);
            cs.EnableKeyword(_ascendKeyword);
            cs.EnableKeyword(_sortPairKeyword);
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Vulkan)
                cs.EnableKeyword(_vulkanKeyword);
            else
                cs.DisableKeyword(_vulkanKeyword);
        }

        static uint DivRoundUp(uint x, uint y) => (x + y - 1) / y;

        //Can we remove the last 4 padding without breaking?
        struct SortConstants
        {
            public uint numKeys;                        // The number of keys to sort
            public uint radixShift;                     // The radix shift value for the current pass
            public uint threadBlocks;                   // threadBlocks
            public uint padding0;                       // Padding - unused
        }

        public void Dispatch(CommandBuffer cmd, GpuSortArgs args)
        {
            if (!Valid)
                return;

            GraphicsBuffer srcKeyBuffer = args.inputKeys;
            GraphicsBuffer srcPayloadBuffer = args.inputValues;
            GraphicsBuffer dstKeyBuffer = args.resources.altKeys;
            GraphicsBuffer dstPayloadBuffer = args.resources.altValues;

            SortConstants constants = default;
            constants.numKeys = args.count;
            constants.threadBlocks = DivRoundUp(args.count, DEVICE_RADIX_SORT_PARTITION_SIZE);

            // Setup overall constants
            cmd.SetComputeIntParam(_cs, "e_numKeys", (int)constants.numKeys);
            cmd.SetComputeIntParam(_cs, "e_threadBlocks", (int)constants.threadBlocks);

            //Set statically located buffers
            //Upsweep
            cmd.SetComputeBufferParam(_cs, _kernelUpsweep, "b_passHist", args.resources.histogram);
            cmd.SetComputeBufferParam(_cs, _kernelUpsweep, "b_globalHist", args.resources.scratch);

            //Scan
            cmd.SetComputeBufferParam(_cs, _kernelScan, "b_passHist", args.resources.histogram);

            //Downsweep
            cmd.SetComputeBufferParam(_cs, _kernelDownsweep, "b_passHist", args.resources.histogram);
            cmd.SetComputeBufferParam(_cs, _kernelDownsweep, "b_globalHist", args.resources.scratch);

            //Clear the global histogram
            cmd.SetComputeBufferParam(_cs, _kernelInit, "b_globalHist", args.resources.scratch);
            cmd.DispatchCompute(_cs, _kernelInit, 1, 1, 1);

            // Execute the sort algorithm in 8-bit increments starting at
            // args.sortStartBit (see GpuSortArgs for the contract).
            Assert.IsTrue(args.sortStartBit % 16 == 0 && args.sortStartBit < 32);
            for (constants.radixShift = args.sortStartBit; constants.radixShift < 32; constants.radixShift += DEVICE_RADIX_SORT_BITS)
            {
                cmd.SetComputeIntParam(_cs, "e_radixShift", (int)constants.radixShift);

                //Upsweep
                cmd.SetComputeBufferParam(_cs, _kernelUpsweep, "b_sort", srcKeyBuffer);
                cmd.DispatchCompute(_cs, _kernelUpsweep, (int)constants.threadBlocks, 1, 1);

                // Scan
                cmd.DispatchCompute(_cs, _kernelScan, (int)DEVICE_RADIX_SORT_RADIX, 1, 1);

                // Downsweep
                cmd.SetComputeBufferParam(_cs, _kernelDownsweep, "b_sort", srcKeyBuffer);
                cmd.SetComputeBufferParam(_cs, _kernelDownsweep, "b_sortPayload", srcPayloadBuffer);
                cmd.SetComputeBufferParam(_cs, _kernelDownsweep, "b_alt", dstKeyBuffer);
                cmd.SetComputeBufferParam(_cs, _kernelDownsweep, "b_altPayload", dstPayloadBuffer);
                cmd.DispatchCompute(_cs, _kernelDownsweep, (int)constants.threadBlocks, 1, 1);

                // Swap
                (srcKeyBuffer, dstKeyBuffer) = (dstKeyBuffer, srcKeyBuffer);
                (srcPayloadBuffer, dstPayloadBuffer) = (dstPayloadBuffer, srcPayloadBuffer);
            }
        }
    }
}
