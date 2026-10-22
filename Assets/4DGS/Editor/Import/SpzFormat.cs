using Unity.Mathematics;

namespace GaussianSplatting.Editor.Import
{
    // Constants and per-attribute encodings of the Niantic SPZ format
    // (https://github.com/nianticlabs/spz), shared by reader and writer.
    //
    // Containers: versions 1-3 are one gzip stream of [16 byte header]
    // [positions][alphas][colors][scales][rotations][sh]; version 4 is a
    // plaintext 32 byte header, optional extensions, a table of contents and
    // the same six attribute streams each compressed with zstd.
    //
    // SPZ stores RUB coordinates while the 3DGS PLY convention (and so this
    // project's asset data) is RDF: converting either way negates y and z,
    // which is a 180 degree rotation about x. The flip tables below are that
    // conversion; it is its own inverse.
    public static class SpzFormat
    {
        public const uint Magic = 0x5053474e; // "NGSP"
        public const int LatestVersion = 4;
        public const int MinSmallestThreeVersion = 3;
        public const int MinZstdVersion = 4;
        public const int LegacyHeaderSize = 16;
        public const int HeaderSize = 32;
        public const int StreamCount = 6;
        public const byte FlagAntialiased = 0x1;
        public const byte FlagHasExtensions = 0x2;
        public const float ColorScale = 0.15f;
        // SPZ allows degree 4; this project keeps up to 3
        public const int MaxFileSHDegree = 4;
        public const int DefaultFractionalBits = 12;

        // Plain functions rather than static arrays so Burst jobs can use them.
        public static float3 FlipP => new(1, -1, -1);
        // Applied to the quaternion's x,y,z (w unchanged)
        public static float3 FlipQ => new(1, -1, -1);

        // Sign of SH coefficient k (bands 1-3, k < 15) under y,z negation:
        // negative for k = 0,1,3,6,8,10,11,13.
        public static float FlipSH(int k) => ((0x2D4B >> k) & 1) != 0 ? -1f : 1f;

        public static int SHCoeffsForDegree(int degree)
        {
            return degree switch
            {
                1 => 3,
                2 => 8,
                3 => 15,
                4 => 24,
                _ => 0
            };
        }

        public static int RotationBytes(int version) => version >= MinSmallestThreeVersion ? 4 : 3;

        // std::round semantics (half away from zero), which the reference
        // encoder uses; math.round rounds half to even.
        public static float RoundHalfAway(float x) => math.sign(x) * math.floor(math.abs(x) + 0.5f);

        public static byte ToUInt8(float x) => (byte)math.clamp(RoundHalfAway(x), 0f, 255f);
    }
}
