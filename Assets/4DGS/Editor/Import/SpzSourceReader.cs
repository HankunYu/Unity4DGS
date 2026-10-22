// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.Import
{
    // Reads Niantic/Scaniverse .spz files, versions 2-4:
    // https://github.com/nianticlabs/spz
    // Splats are converted from SPZ's RUB to the PLY convention (RDF), so an
    // SPZ and a PLY of the same scene import identically.
    [BurstCompile]
    public class SpzSourceReader : ISplatSourceReader
    {
        public SplatSourceHeader ReadHeader(string filePath)
        {
            using var header = SpzContainer.ReadHeader(filePath);
            return ToSourceHeader(header);
        }

        public NativeArray<InputSplatData> Read(string filePath, out SplatSourceHeader header)
        {
            using var packed = SpzContainer.Read(filePath);
            header = ToSourceHeader(packed);
            var splats = new NativeArray<InputSplatData>(packed.numPoints, Allocator.Persistent);
            new UnpackJob
            {
                positions = packed.positions,
                alphas = packed.alphas,
                colors = packed.colors,
                scales = packed.scales,
                rotations = packed.rotations,
                sh = packed.sh,
                fileSHCoeffs = SpzFormat.SHCoeffsForDegree(packed.shDegree),
                smallestThree = packed.version >= SpzFormat.MinSmallestThreeVersion,
                positionScale = 1.0f / (1 << packed.fractionalBits),
                splats = splats,
            }.Schedule(packed.numPoints, 4096).Complete();
            return splats;
        }

        static SplatSourceHeader ToSourceHeader(SpzPackedData packed)
        {
            // Degree 4 bands are dropped on import.
            return new SplatSourceHeader { splatCount = packed.numPoints, shDegree = math.min(packed.shDegree, 3) };
        }

        [BurstCompile]
        struct UnpackJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<byte> positions;
            [ReadOnly] public NativeArray<byte> alphas;
            [ReadOnly] public NativeArray<byte> colors;
            [ReadOnly] public NativeArray<byte> scales;
            [ReadOnly] public NativeArray<byte> rotations;
            [ReadOnly] public NativeArray<byte> sh;
            public int fileSHCoeffs;
            public bool smallestThree;
            public float positionScale;
            [WriteOnly] public NativeArray<InputSplatData> splats;

            public void Execute(int index)
            {
                var splat = new InputSplatData();

                float3 pos = new float3(UnpackFixed24(index * 3 + 0), UnpackFixed24(index * 3 + 1), UnpackFixed24(index * 3 + 2));
                splat.pos = pos * positionScale * SpzFormat.FlipP;

                float3 logScale = new float3(scales[index * 3 + 0], scales[index * 3 + 1], scales[index * 3 + 2]) / 16.0f - 10.0f;
                splat.scale = GaussianUtils.LinearScale(logScale);

                float4 q = smallestThree ? UnpackSmallestThree(index) : UnpackFirstThree(index);
                q.xyz *= SpzFormat.FlipQ;
                float4 packedRot = GaussianUtils.PackSmallest3Rotation(math.normalize(q));
                splat.rot = new Quaternion(packedRot.x, packedRot.y, packedRot.z, packedRot.w);

                splat.opacity = alphas[index] / 255.0f;

                float3 dc = (new float3(colors[index * 3 + 0], colors[index * 3 + 1], colors[index * 3 + 2]) / 255.0f - 0.5f) / SpzFormat.ColorScale;
                splat.dc0 = GaussianUtils.SH0ToColor(dc);

                // The file stores fileSHCoeffs coefficients per splat; higher
                // ones stay zero, and degree 4 ones (k >= 15) are skipped.
                int shBase = index * fileSHCoeffs * 3;
                splat.sh1 = SHCoeff(shBase, 0);
                splat.sh2 = SHCoeff(shBase, 1);
                splat.sh3 = SHCoeff(shBase, 2);
                splat.sh4 = SHCoeff(shBase, 3);
                splat.sh5 = SHCoeff(shBase, 4);
                splat.sh6 = SHCoeff(shBase, 5);
                splat.sh7 = SHCoeff(shBase, 6);
                splat.sh8 = SHCoeff(shBase, 7);
                splat.sh9 = SHCoeff(shBase, 8);
                splat.shA = SHCoeff(shBase, 9);
                splat.shB = SHCoeff(shBase, 10);
                splat.shC = SHCoeff(shBase, 11);
                splat.shD = SHCoeff(shBase, 12);
                splat.shE = SHCoeff(shBase, 13);
                splat.shF = SHCoeff(shBase, 14);

                splats[index] = splat;
            }

            float UnpackFixed24(int component)
            {
                int fx = positions[component * 3 + 0] | (positions[component * 3 + 1] << 8) | (positions[component * 3 + 2] << 16);
                fx |= (fx & 0x800000) != 0 ? -16777216 : 0; // sign extension with 0xff000000
                return fx;
            }

            // v2: x,y,z as signed bytes, w >= 0 derived
            float4 UnpackFirstThree(int index)
            {
                float3 xyz = new float3(rotations[index * 3 + 0], rotations[index * 3 + 1], rotations[index * 3 + 2]) / 127.5f - 1.0f;
                return new float4(xyz, math.sqrt(math.max(0.0f, 1.0f - math.lengthsq(xyz))));
            }

            // v3+: 2 bits index of the largest component, then the other
            // three as sign + 9-bit magnitude scaled by 1/sqrt(2), packed from
            // the highest remaining index down.
            float4 UnpackSmallestThree(int index)
            {
                uint comp = rotations[index * 4 + 0] | ((uint)rotations[index * 4 + 1] << 8) |
                            ((uint)rotations[index * 4 + 2] << 16) | ((uint)rotations[index * 4 + 3] << 24);
                const uint kMask = (1u << 9) - 1u;
                int largest = (int)(comp >> 30);
                float4 q = 0;
                float sumSquares = 0;
                for (int i = 3; i >= 0; --i)
                {
                    if (i == largest)
                        continue;
                    uint mag = comp & kMask;
                    bool negative = ((comp >> 9) & 1u) != 0;
                    comp >>= 10;
                    float v = math.SQRT2 * 0.5f * mag / kMask;
                    q[i] = negative ? -v : v;
                    sumSquares += v * v;
                }
                q[largest] = math.sqrt(math.max(0.0f, 1.0f - sumSquares));
                return q;
            }

            Vector3 SHCoeff(int shBase, int k)
            {
                if (k >= fileSHCoeffs)
                    return Vector3.zero;
                int i = shBase + k * 3;
                float3 v = (new float3(sh[i], sh[i + 1], sh[i + 2]) - 128.0f) / 128.0f;
                return v * SpzFormat.FlipSH(k);
            }
        }
    }
}
