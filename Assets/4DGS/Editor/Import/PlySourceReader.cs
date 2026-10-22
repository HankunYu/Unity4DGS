using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Assertions;

namespace GaussianSplatting.Editor.Import
{
    // Reads the 3DGS reference PLY layout (binary little endian).
    [BurstCompile]
    public class PlySourceReader : ISplatSourceReader
    {
        static readonly string[] s_RequiredAttributes = { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" };

        public SplatSourceHeader ReadHeader(string filePath)
        {
            PlyFileParser.ReadFileHeader(filePath, out int vertexCount, out _, out var attributes);
            return new SplatSourceHeader
            {
                splatCount = vertexCount,
                shDegree = SHDegreeForCoeffs(SHCoeffsPerChannel(attributes)),
            };
        }

        public unsafe NativeArray<InputSplatData> Read(string filePath, out SplatSourceHeader header)
        {
            PlyFileParser.ReadFile(filePath, out int splatCount, out int vertexStride, out var attributes, out NativeArray<byte> plyRawData);
            NativeArray<InputSplatData> splats;
            try
            {
                string attrError = CheckAttributes(attributes);
                if (!string.IsNullOrEmpty(attrError))
                    throw new IOException($"PLY file is probably not a Gaussian Splat file? Missing properties: {attrError}");
                splats = PlyDataToSplats(plyRawData, splatCount, vertexStride, attributes);
            }
            finally
            {
                plyRawData.Dispose();
            }
            int coeffsPerChannel = SHCoeffsPerChannel(attributes);
            ReorderSHs(splatCount, (float*)splats.GetUnsafePtr(), coeffsPerChannel);
            LinearizeData(splats);
            header = new SplatSourceHeader
            {
                splatCount = splatCount,
                shDegree = SHDegreeForCoeffs(coeffsPerChannel),
            };
            return splats;
        }

        static string CheckAttributes(List<(string, PlyFileParser.ElementType)> attributes)
        {
            List<string> missing = s_RequiredAttributes.Where(req => !attributes.Contains((req, PlyFileParser.ElementType.Float))).ToList();
            if (missing.Count == 0)
                return null;
            return string.Join(",", missing);
        }

        static unsafe NativeArray<InputSplatData> PlyDataToSplats(NativeArray<byte> input, int count, int stride, List<(string, PlyFileParser.ElementType)> attributes)
        {
            NativeArray<int> fileAttrOffsets = new NativeArray<int>(attributes.Count, Allocator.Temp);
            int offset = 0;
            for (var ai = 0; ai < attributes.Count; ai++)
            {
                var attr = attributes[ai];
                fileAttrOffsets[ai] = offset;
                offset += PlyFileParser.TypeToSize(attr.Item2);
            }

            // InputSplatData field order, one entry per float
            var splatAttributes = new List<string> { "x", "y", "z", "nx", "ny", "nz", "f_dc_0", "f_dc_1", "f_dc_2" };
            for (int i = 0; i < 45; ++i)
                splatAttributes.Add($"f_rest_{i}");
            splatAttributes.AddRange(new[] { "opacity", "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" });
            Assert.AreEqual(UnsafeUtility.SizeOf<InputSplatData>() / 4, splatAttributes.Count);

            NativeArray<int> srcOffsets = new NativeArray<int>(splatAttributes.Count, Allocator.Temp);
            for (int ai = 0; ai < splatAttributes.Count; ai++)
            {
                int attrIndex = attributes.IndexOf((splatAttributes[ai], PlyFileParser.ElementType.Float));
                srcOffsets[ai] = attrIndex >= 0 ? fileAttrOffsets[attrIndex] : -1;
            }

            NativeArray<InputSplatData> dst = new NativeArray<InputSplatData>(count, Allocator.Persistent);
            ReorderPlyData(count, (byte*)input.GetUnsafeReadOnlyPtr(), stride, (byte*)dst.GetUnsafePtr(), UnsafeUtility.SizeOf<InputSplatData>(), (int*)srcOffsets.GetUnsafeReadOnlyPtr());
            return dst;
        }

        [BurstCompile]
        static unsafe void ReorderPlyData(int splatCount, byte* src, int srcStride, byte* dst, int dstStride, int* srcOffsets)
        {
            for (int i = 0; i < splatCount; i++)
            {
                for (int attr = 0; attr < dstStride / 4; attr++)
                {
                    if (srcOffsets[attr] >= 0)
                        *(int*)(dst + attr * 4) = *(int*)(src + srcOffsets[attr]);
                }
                src += srcStride;
                dst += dstStride;
            }
        }

        // f_rest_* holds the higher SH bands channel-major (all R, then all G,
        // then all B), so the per-channel count depends on the file's SH
        // degree: 0, 3, 8 or 15 coefficients.
        static int SHCoeffsPerChannel(List<(string, PlyFileParser.ElementType)> attributes)
        {
            int restCount = attributes.Count(a => a.Item1.StartsWith("f_rest_") && a.Item2 == PlyFileParser.ElementType.Float);
            return math.min(restCount / 3, 15);
        }

        // Highest degree whose bands are all present.
        static int SHDegreeForCoeffs(int coeffsPerChannel)
        {
            if (coeffsPerChannel >= 15)
                return 3;
            if (coeffsPerChannel >= 8)
                return 2;
            if (coeffsPerChannel >= 3)
                return 1;
            return 0;
        }

        // Converts the channel-major f_rest_* block into per-coefficient RGB
        // triplets (sh1..shF). Bands the file does not have stay zero.
        [BurstCompile]
        static unsafe void ReorderSHs(int splatCount, float* data, int coeffsPerChannel)
        {
            int splatStride = UnsafeUtility.SizeOf<InputSplatData>() / 4;
            int shStartOffset = 9, shCount = 15;
            float* tmp = stackalloc float[shCount * 3];
            int idx = shStartOffset;
            for (int i = 0; i < splatCount; ++i)
            {
                for (int j = 0; j < shCount; ++j)
                {
                    bool present = j < coeffsPerChannel;
                    tmp[j * 3 + 0] = present ? data[idx + j] : 0;
                    tmp[j * 3 + 1] = present ? data[idx + j + coeffsPerChannel] : 0;
                    tmp[j * 3 + 2] = present ? data[idx + j + coeffsPerChannel * 2] : 0;
                }

                for (int j = 0; j < shCount * 3; ++j)
                {
                    data[idx + j] = tmp[j];
                }

                idx += splatStride;
            }
        }

        [BurstCompile]
        struct LinearizeDataJob : IJobParallelFor
        {
            public NativeArray<InputSplatData> splatData;

            public void Execute(int index)
            {
                var splat = splatData[index];

                var q = splat.rot;
                var qq = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));
                qq = GaussianUtils.PackSmallest3Rotation(qq);
                splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

                splat.scale = GaussianUtils.LinearScale(splat.scale);

                splat.dc0 = GaussianUtils.SH0ToColor(splat.dc0);
                splat.opacity = GaussianUtils.Sigmoid(splat.opacity);

                splatData[index] = splat;
            }
        }

        static void LinearizeData(NativeArray<InputSplatData> splatData)
        {
            new LinearizeDataJob { splatData = splatData }.Schedule(splatData.Length, 4096).Complete();
        }
    }
}
