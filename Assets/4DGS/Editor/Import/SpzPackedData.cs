using System;
using Unity.Collections;

namespace GaussianSplatting.Editor.Import
{
    // The six SPZ attribute streams, still quantized, plus header fields.
    public sealed class SpzPackedData : IDisposable
    {
        public int version;
        public int numPoints;
        // As stored in the file (0..4)
        public int shDegree;
        public int fractionalBits;
        public bool antialiased;

        public NativeArray<byte> positions; // 3 x 24-bit fixed point
        public NativeArray<byte> alphas;    // sigmoid(opacity) * 255
        public NativeArray<byte> colors;    // SH DC, scaled by ColorScale around 0.5
        public NativeArray<byte> scales;    // (log scale + 10) * 16
        public NativeArray<byte> rotations; // 3 (v2) or 4 (v3+) bytes
        public NativeArray<byte> sh;        // per coefficient RGB triplets

        public void Allocate()
        {
            int shCoeffs = SpzFormat.SHCoeffsForDegree(shDegree);
            positions = new NativeArray<byte>(numPoints * 9, Allocator.Persistent);
            alphas = new NativeArray<byte>(numPoints, Allocator.Persistent);
            colors = new NativeArray<byte>(numPoints * 3, Allocator.Persistent);
            scales = new NativeArray<byte>(numPoints * 3, Allocator.Persistent);
            rotations = new NativeArray<byte>(numPoints * SpzFormat.RotationBytes(version), Allocator.Persistent);
            sh = new NativeArray<byte>(numPoints * shCoeffs * 3, Allocator.Persistent);
        }

        // Streams in file order
        public NativeArray<byte>[] Streams => new[] { positions, alphas, colors, scales, rotations, sh };

        public void Dispose()
        {
            if (positions.IsCreated) positions.Dispose();
            if (alphas.IsCreated) alphas.Dispose();
            if (colors.IsCreated) colors.Dispose();
            if (scales.IsCreated) scales.Dispose();
            if (rotations.IsCreated) rotations.Dispose();
            if (sh.IsCreated) sh.Dispose();
        }
    }
}
