using System;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GaussianSplatting.Editor.Import
{
    // Writes linear-space splats (the same data SpzSourceReader produces)
    // as an .spz file, converting from RDF to SPZ's RUB.
    [BurstCompile]
    public static class SpzWriter
    {
        [Serializable]
        public struct Options
        {
            // 2-4; 3 is the newest gzip version and the most widely readable
            public int version;
            // 1-8 bits kept per SH coefficient; fewer compress better
            public int sh1Bits;
            public int shRestBits;
            // Marks data trained with a Mip-Splatting style 2D filter
            public bool antialiased;

            // Same quantization as the reference encoder
            public static Options Default => new() { version = 3, sh1Bits = 5, shRestBits = 4 };
            // Keeps all 8 bits of every SH coefficient
            public static Options Lossless => new() { version = 3, sh1Bits = 8, shRestBits = 8 };
        }

        public static void Write(string filePath, NativeArray<InputSplatData> splats, int shDegree, Options options)
        {
            if (options.version < 2 || options.version > SpzFormat.LatestVersion)
                throw new ArgumentOutOfRangeException(nameof(options), $"SPZ version {options.version} cannot be written");
            if (options.sh1Bits < 1 || options.sh1Bits > 8 || options.shRestBits < 1 || options.shRestBits > 8)
                throw new ArgumentOutOfRangeException(nameof(options), "SH bits must be 1-8");
            if (shDegree < 0 || shDegree > 3)
                throw new ArgumentOutOfRangeException(nameof(shDegree));

            using var packed = new SpzPackedData
            {
                version = options.version,
                numPoints = splats.Length,
                shDegree = shDegree,
                fractionalBits = FractionalBitsFor(splats),
                antialiased = options.antialiased,
            };
            packed.Allocate();
            new PackJob
            {
                splats = splats,
                positions = packed.positions,
                alphas = packed.alphas,
                colors = packed.colors,
                scales = packed.scales,
                rotations = packed.rotations,
                sh = packed.sh,
                shCoeffs = SpzFormat.SHCoeffsForDegree(shDegree),
                smallestThree = options.version >= SpzFormat.MinSmallestThreeVersion,
                positionScale = 1 << packed.fractionalBits,
                sh1Bucket = 1 << (8 - options.sh1Bits),
                shRestBucket = 1 << (8 - options.shRestBits),
            }.Schedule(splats.Length, 4096).Complete();
            SpzContainer.Write(filePath, packed);
        }

        // The reference encoder always uses 12 bits (~0.25 mm). Scenes that
        // reach past +-2048 units would wrap, so use fewer bits for those.
        static int FractionalBitsFor(NativeArray<InputSplatData> splats)
        {
            float maxAbs = 0;
            for (int i = 0; i < splats.Length; i++)
                maxAbs = math.max(maxAbs, math.cmax(math.abs((float3)splats[i].pos)));
            int bits = SpzFormat.DefaultFractionalBits;
            while (bits > 0 && maxAbs * (1 << bits) >= (1 << 23) - 1)
                bits--;
            return bits;
        }

        [BurstCompile]
        struct PackJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> splats;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<byte> positions;
            [WriteOnly] public NativeArray<byte> alphas;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<byte> colors;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<byte> scales;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<byte> rotations;
            [NativeDisableParallelForRestriction] [WriteOnly] public NativeArray<byte> sh;
            public int shCoeffs;
            public bool smallestThree;
            public float positionScale;
            public int sh1Bucket;
            public int shRestBucket;

            public void Execute(int index)
            {
                var splat = splats[index];

                float3 pos = (float3)splat.pos * SpzFormat.FlipP * positionScale;
                for (int c = 0; c < 3; c++)
                {
                    int fixed32 = (int)math.clamp(SpzFormat.RoundHalfAway(pos[c]), -(1 << 23), (1 << 23) - 1);
                    int o = (index * 3 + c) * 3;
                    positions[o + 0] = (byte)(fixed32 & 0xff);
                    positions[o + 1] = (byte)((fixed32 >> 8) & 0xff);
                    positions[o + 2] = (byte)((fixed32 >> 16) & 0xff);
                }

                float3 logScale = math.log(math.max((float3)splat.scale, 1e-30f));
                for (int c = 0; c < 3; c++)
                    scales[index * 3 + c] = SpzFormat.ToUInt8((logScale[c] + 10.0f) * 16.0f);

                var r = splat.rot;
                float4 q = math.normalize(GaussianUtils.UnpackSmallest3Rotation(new float4(r.x, r.y, r.z, r.w)));
                q.xyz *= SpzFormat.FlipQ;
                if (smallestThree)
                    PackSmallestThree(index, q);
                else
                    PackFirstThree(index, q);

                alphas[index] = SpzFormat.ToUInt8(splat.opacity * 255.0f);

                float3 dc = GaussianUtils.ColorToSH0(splat.dc0);
                for (int c = 0; c < 3; c++)
                    colors[index * 3 + c] = SpzFormat.ToUInt8(dc[c] * (SpzFormat.ColorScale * 255.0f) + 0.5f * 255.0f);

                int shBase = index * shCoeffs * 3;
                for (int k = 0; k < shCoeffs; k++)
                {
                    float3 v = SHCoeff(splat, k) * SpzFormat.FlipSH(k);
                    int bucket = k < 3 ? sh1Bucket : shRestBucket;
                    for (int c = 0; c < 3; c++)
                        sh[shBase + k * 3 + c] = QuantizeSH(v[c], bucket);
                }
            }

            // q.w >= 0 is implied; store x,y,z of the matching hemisphere
            void PackFirstThree(int index, float4 q)
            {
                q *= q.w < 0 ? -127.5f : 127.5f;
                q += 127.5f;
                rotations[index * 3 + 0] = SpzFormat.ToUInt8(q.x);
                rotations[index * 3 + 1] = SpzFormat.ToUInt8(q.y);
                rotations[index * 3 + 2] = SpzFormat.ToUInt8(q.z);
            }

            void PackSmallestThree(int index, float4 q)
            {
                int largest = 0;
                for (int i = 1; i < 4; i++)
                {
                    if (math.abs(q[i]) > math.abs(q[largest]))
                        largest = i;
                }
                // -q is the same rotation; flip so the largest is positive
                bool negate = q[largest] < 0;
                uint comp = (uint)largest;
                for (int i = 0; i < 4; i++)
                {
                    if (i == largest)
                        continue;
                    uint negBit = (q[i] < 0) ^ negate ? 1u : 0u;
                    uint mag = (uint)((float)((1u << 9) - 1u) * (math.abs(q[i]) / (math.SQRT2 * 0.5f)) + 0.5f);
                    comp = (comp << 10) | (negBit << 9) | mag;
                }
                rotations[index * 4 + 0] = (byte)(comp & 0xff);
                rotations[index * 4 + 1] = (byte)((comp >> 8) & 0xff);
                rotations[index * 4 + 2] = (byte)((comp >> 16) & 0xff);
                rotations[index * 4 + 3] = (byte)((comp >> 24) & 0xff);
            }

            // To 8 bits, then to the nearest bucket center so 0 stays exact
            static byte QuantizeSH(float x, int bucketSize)
            {
                int q = (int)(SpzFormat.RoundHalfAway(x * 128.0f) + 128.0f);
                q = (q + bucketSize / 2) / bucketSize * bucketSize;
                return (byte)math.clamp(q, 0, 255);
            }

            static float3 SHCoeff(InputSplatData s, int k)
            {
                return k switch
                {
                    0 => s.sh1, 1 => s.sh2, 2 => s.sh3, 3 => s.sh4, 4 => s.sh5,
                    5 => s.sh6, 6 => s.sh7, 7 => s.sh8, 8 => s.sh9, 9 => s.shA,
                    10 => s.shB, 11 => s.shC, 12 => s.shD, 13 => s.shE, _ => s.shF,
                };
            }
        }
    }
}
