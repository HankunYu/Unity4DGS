using System;
using GaussianSplatting.Runtime;
using Unity.Collections;

namespace GaussianSplatting.Editor.Import
{
    // Output of GaussianSplatAssetBuilder: an initialized asset (metadata and
    // data hash set, data blocks not yet attached) plus the encoded blocks.
    // The front end decides how the blocks become TextAssets.
    public sealed class GaussianSplatBuildResult : IDisposable
    {
        public GaussianSplatAsset asset;
        // Not created when the formats are lossless (no chunk table)
        public NativeArray<byte> chunkData;
        public NativeArray<byte> posData;
        public NativeArray<byte> otherData;
        public NativeArray<byte> colorData;
        public NativeArray<byte> shData;

        public void Dispose()
        {
            DisposeBlock(ref chunkData);
            DisposeBlock(ref posData);
            DisposeBlock(ref otherData);
            DisposeBlock(ref colorData);
            DisposeBlock(ref shData);
        }

        static void DisposeBlock(ref NativeArray<byte> block)
        {
            if (block.IsCreated)
                block.Dispose();
            block = default;
        }
    }
}
