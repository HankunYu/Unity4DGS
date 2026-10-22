// SPDX-License-Identifier: MIT

using System.IO;
using Unity.Collections;
using System.IO.Compression;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.Import
{
    // reads Niantic/Scaniverse .SPZ files:
    // https://github.com/nianticlabs/spz
    // https://scaniverse.com/spz
    [BurstCompile]
    public class SpzSourceReader : ISplatSourceReader
    {
        struct SpzHeader {
            public uint magic; // 0x5053474e "NGSP"
            public uint version; // 2
            public uint numPoints;
            public uint sh_fracbits_flags_reserved;
        };

        public SplatSourceHeader ReadHeader(string filePath)
        {
            if (!File.Exists(filePath))
                return default;
            using var fs = File.OpenRead(filePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            ReadHeaderImpl(filePath, gz, out int vertexCount, out int shLevel, out _, out _);
            return new SplatSourceHeader { splatCount = vertexCount, shDegree = shLevel };
        }

        static void ReadHeaderImpl(string filePath, Stream fs, out int vertexCount, out int shLevel, out int fractBits, out int flags)
        {
            var header = new NativeArray<SpzHeader>(1, Allocator.Temp);
            var readBytes = fs.Read(header.Reinterpret<byte>(16));
            if (readBytes != 16)
                throw new IOException($"SPZ {filePath} read error, failed to read header");

            if (header[0].magic != 0x5053474e)
                throw new IOException($"SPZ {filePath} read error, header magic unexpected {header[0].magic}");
            if (header[0].version != 2)
                throw new IOException($"SPZ {filePath} read error, header version unexpected {header[0].version}");

            vertexCount = (int)header[0].numPoints;
            shLevel = (int)(header[0].sh_fracbits_flags_reserved & 0xFF);
            fractBits = (int)((header[0].sh_fracbits_flags_reserved >> 8) & 0xFF);
            flags = (int)((header[0].sh_fracbits_flags_reserved >> 16) & 0xFF);
        }

        static int SHCoeffsForLevel(int level)
        {
            return level switch
            {
                0 => 0,
                1 => 3,
                2 => 8,
                3 => 15,
                _ => 0
            };
        }

        public NativeArray<InputSplatData> Read(string filePath, out SplatSourceHeader header)
        {
            using var fs = File.OpenRead(filePath);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            ReadHeaderImpl(filePath, gz, out var splatCount, out var shLevel, out var fractBits, out var flags);

            if (splatCount < 1 || splatCount > 10_000_000) // 10M hardcoded in SPZ code
                throw new IOException($"SPZ {filePath} read error, out of range splat count {splatCount}");
            if (shLevel < 0 || shLevel > 3)
                throw new IOException($"SPZ {filePath} read error, out of range SH level {shLevel}");
            if (fractBits < 0 || fractBits > 24)
                throw new IOException($"SPZ {filePath} read error, out of range fractional bits {fractBits}");

            // allocate temporary storage
            int shCoeffs = SHCoeffsForLevel(shLevel);
            NativeArray<byte> packedPos = new(splatCount * 3 * 3, Allocator.Persistent);
            NativeArray<byte> packedScale = new(splatCount * 3, Allocator.Persistent);
            NativeArray<byte> packedRot = new(splatCount * 3, Allocator.Persistent);
            NativeArray<byte> packedAlpha = new(splatCount, Allocator.Persistent);
            NativeArray<byte> packedCol = new(splatCount * 3, Allocator.Persistent);
            NativeArray<byte> packedSh = new(splatCount * 3 * shCoeffs, Allocator.Persistent);

            NativeArray<InputSplatData> splats;
            try
            {
                // read file contents into temporaries
                bool readOk = ReadFully(gz, packedPos) && ReadFully(gz, packedAlpha) && ReadFully(gz, packedCol) &&
                              ReadFully(gz, packedScale) && ReadFully(gz, packedRot) && ReadFully(gz, packedSh);
                if (!readOk)
                    throw new IOException($"SPZ {filePath} read error, file smaller than it should be");

                // unpack into full splat data
                splats = new NativeArray<InputSplatData>(splatCount, Allocator.Persistent);
                UnpackDataJob job = new UnpackDataJob();
                job.packedPos = packedPos;
                job.packedScale = packedScale;
                job.packedRot = packedRot;
                job.packedAlpha = packedAlpha;
                job.packedCol = packedCol;
                job.packedSh = packedSh;
                job.shCoeffs = shCoeffs;
                job.fractScale = 1.0f / (1 << fractBits);
                job.splats = splats;
                job.Schedule(splatCount, 4096).Complete();
            }
            finally
            {
                // A corrupt stream throws from inside the reads; the
                // temporaries are released either way.
                packedPos.Dispose();
                packedScale.Dispose();
                packedRot.Dispose();
                packedAlpha.Dispose();
                packedCol.Dispose();
                packedSh.Dispose();
            }
            header = new SplatSourceHeader { splatCount = splatCount, shDegree = shLevel };
            return splats;
        }

        // Stream.Read may return fewer bytes than asked (GZipStream does,
        // at block boundaries) without being at the end: keep reading.
        static bool ReadFully(Stream stream, NativeArray<byte> dst)
        {
            int total = 0;
            while (total < dst.Length)
            {
                int read = stream.Read(dst.GetSubArray(total, dst.Length - total));
                if (read <= 0)
                    return false;
                total += read;
            }
            return true;
        }

        [BurstCompile]
        struct UnpackDataJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedPos;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedScale;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedRot;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedAlpha;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedCol;
            [NativeDisableParallelForRestriction] [ReadOnly] public NativeArray<byte> packedSh;
            public float fractScale;
            public int shCoeffs;
            public NativeArray<InputSplatData> splats;

            public void Execute(int index)
            {
                var splat = splats[index];

                splat.pos = new Vector3(UnpackFloat(index * 3 + 0) * fractScale, UnpackFloat(index * 3 + 1) * fractScale, UnpackFloat(index * 3 + 2) * fractScale);

                splat.scale = new Vector3(packedScale[index * 3 + 0], packedScale[index * 3 + 1], packedScale[index * 3 + 2]) / 16.0f - new Vector3(10.0f, 10.0f, 10.0f);
                splat.scale = GaussianUtils.LinearScale(splat.scale);

                Vector3 xyz = new Vector3(packedRot[index * 3 + 0], packedRot[index * 3 + 1], packedRot[index * 3 + 2]) * (1.0f / 127.5f) - new Vector3(1, 1, 1);
                float w = math.sqrt(math.max(0.0f, 1.0f - xyz.sqrMagnitude));
                var q = new float4(xyz.x, xyz.y, xyz.z, w);
                var qq = math.normalize(q);
                qq = GaussianUtils.PackSmallest3Rotation(qq);
                splat.rot = new Quaternion(qq.x, qq.y, qq.z, qq.w);

                splat.opacity = packedAlpha[index] / 255.0f;

                Vector3 col = new Vector3(packedCol[index * 3 + 0], packedCol[index * 3 + 1], packedCol[index * 3 + 2]);
                col = col / 255.0f - new Vector3(0.5f, 0.5f, 0.5f);
                col /= 0.15f;
                splat.dc0 = GaussianUtils.SH0ToColor(col);

                // The file only stores shCoeffs coefficients per splat (0/3/8/15
                // depending on SH level) — coefficients beyond that must be zero,
                // not reads past this splat's range (data corruption / OOB).
                int shBase = index * shCoeffs * 3;
                splat.sh1 = UnpackSHCoeff(shBase, 0);
                splat.sh2 = UnpackSHCoeff(shBase, 1);
                splat.sh3 = UnpackSHCoeff(shBase, 2);
                splat.sh4 = UnpackSHCoeff(shBase, 3);
                splat.sh5 = UnpackSHCoeff(shBase, 4);
                splat.sh6 = UnpackSHCoeff(shBase, 5);
                splat.sh7 = UnpackSHCoeff(shBase, 6);
                splat.sh8 = UnpackSHCoeff(shBase, 7);
                splat.sh9 = UnpackSHCoeff(shBase, 8);
                splat.shA = UnpackSHCoeff(shBase, 9);
                splat.shB = UnpackSHCoeff(shBase, 10);
                splat.shC = UnpackSHCoeff(shBase, 11);
                splat.shD = UnpackSHCoeff(shBase, 12);
                splat.shE = UnpackSHCoeff(shBase, 13);
                splat.shF = UnpackSHCoeff(shBase, 14);

                splats[index] = splat;
            }

            float UnpackFloat(int idx)
            {
                int fx = packedPos[idx * 3 + 0] | (packedPos[idx * 3 + 1] << 8) | (packedPos[idx * 3 + 2] << 16);
                fx |= (fx & 0x800000) != 0 ? -16777216 : 0; // sign extension with 0xff000000
                return fx;
            }

            Vector3 UnpackSH(int idx)
            {
                Vector3 sh = new Vector3(packedSh[idx], packedSh[idx + 1], packedSh[idx + 2]) - new Vector3(128.0f, 128.0f, 128.0f);
                sh /= 128.0f;
                return sh;
            }

            Vector3 UnpackSHCoeff(int shBase, int coeff)
            {
                return coeff < shCoeffs ? UnpackSH(shBase + coeff * 3) : Vector3.zero;
            }
        }

    }
}
