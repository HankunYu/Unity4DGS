using System;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Serialization;

namespace GaussianSplatting.Runtime
{
    public class GaussianSplatAsset : ScriptableObject
    {
        public const int CurrentVersion = 2026_10_23;
        public const int ChunkSize = 256;
        public const int TextureWidth = 2048; // allows up to 32M splats on desktop GPU (2k width x 16k height)
        public const int MaxSplats = 8_600_000; // mostly due to 2GB GPU buffer size limit when exporting a splat (2GB / 248B is just over 8.6M)

        [FormerlySerializedAs("m_FormatVersion"), FormerlySerializedAs("_formatVersion")]
        public int formatVersion;
        [FormerlySerializedAs("m_SplatCount"), FormerlySerializedAs("_splatCount")]
        public int splatCount;
        [FormerlySerializedAs("m_BoundsMin"), FormerlySerializedAs("_boundsMin")]
        public Vector3 boundsMin;
        [FormerlySerializedAs("m_BoundsMax"), FormerlySerializedAs("_boundsMax")]
        public Vector3 boundsMax;
        [FormerlySerializedAs("m_DataHash"), FormerlySerializedAs("_dataHash")]
        public Hash128 dataHash;
        // SH bands stored beyond the base color, 0-3. The SH data holds
        // GetSHCoeffCount(shDegree) coefficients per entry; with 0 there is
        // no SH data at all.
        public int shDegree;

        // Match VECTOR_FMT_* in HLSL
        public enum VectorFormat
        {
            Float32, // 12 bytes: 32F.32F.32F
            Norm16, // 6 bytes: 16.16.16
            Norm11, // 4 bytes: 11.10.11
            Norm6   // 2 bytes: 6.5.5
        }

        public static int GetVectorSize(VectorFormat fmt)
        {
            return fmt switch
            {
                VectorFormat.Float32 => 12,
                VectorFormat.Norm16 => 6,
                VectorFormat.Norm11 => 4,
                VectorFormat.Norm6 => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        public enum ColorFormat
        {
            Float32x4,
            Float16x4,
            Norm8x4,
            BC7,
        }
        public static int GetColorSize(ColorFormat fmt)
        {
            return fmt switch
            {
                ColorFormat.Float32x4 => 16,
                ColorFormat.Float16x4 => 8,
                ColorFormat.Norm8x4 => 4,
                ColorFormat.BC7 => 1,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        public enum SHFormat
        {
            Float32,
            Float16,
            Norm11,
            Norm6,
            Cluster64k,
            Cluster32k,
            Cluster16k,
            Cluster8k,
            Cluster4k,
        }

        public const int MaxSHDegree = 3;

        // SH coefficients per color channel for a degree, excluding the base
        // color: 0, 3, 8, 15
        public static int GetSHCoeffCount(int degree)
        {
            return (degree + 1) * (degree + 1) - 1;
        }

        // Bytes per SH entry (one per splat, or per table item for cluster
        // formats): the coefficients packed back to back, padded to 4 bytes.
        // Must match SplatSHStride in HLSL.
        public static int GetSHStride(SHFormat fmt, int degree)
        {
            int coeffs = GetSHCoeffCount(degree);
            return fmt switch
            {
                SHFormat.Float32 => coeffs * 12,
                SHFormat.Norm11 => coeffs * 4,
                SHFormat.Norm6 => (coeffs * 2 + 3) & ~3,
                // Float16, and the Float16 tables of the cluster formats
                _ => (coeffs * 6 + 3) & ~3,
            };
        }

        // Packed formats as the shaders read them (_SplatFormat): bits 0-7
        // position, 8-15 scale, 16-23 SH format, 24-25 SH degree
        public uint FormatFlags => (uint)posFormat | ((uint)scaleFormat << 8) | ((uint)shFormat << 16) | ((uint)shDegree << 24);

        public long SHDataSize => shData != null ? shData.dataSize : 0;

        public void Initialize(int splats, VectorFormat formatPos, VectorFormat formatScale, ColorFormat formatColor, SHFormat formatSh, int degreeSh, Vector3 bMin, Vector3 bMax, CameraInfo[] cameraInfos)
        {
            splatCount = splats;
            formatVersion = CurrentVersion;
            posFormat = formatPos;
            scaleFormat = formatScale;
            colorFormat = formatColor;
            shFormat = formatSh;
            shDegree = degreeSh;
            cameras = cameraInfos;
            boundsMin = bMin;
            boundsMax = bMax;
        }

        public void SetDataHash(Hash128 hash)
        {
            dataHash = hash;
        }

        public void SetAssetFiles(TextAsset dataChunk, TextAsset dataPos, TextAsset dataOther, TextAsset dataColor, TextAsset dataSh)
        {
            chunkData = dataChunk;
            posData = dataPos;
            otherData = dataOther;
            colorData = dataColor;
            shData = dataSh;
        }

        public static int GetOtherSizeNoSHIndex(VectorFormat scaleFormat)
        {
            return 4 + GetVectorSize(scaleFormat);
        }

        public static int GetOtherSize(VectorFormat scaleFormat, SHFormat shFormat)
        {
            // cluster formats append a 16 bit table index
            return GetOtherSizeNoSHIndex(scaleFormat) + (shFormat >= SHFormat.Cluster64k ? 2 : 0);
        }

        public static int GetSHCount(SHFormat fmt, int splatCount)
        {
            return fmt switch
            {
                SHFormat.Float32 => splatCount,
                SHFormat.Float16 => splatCount,
                SHFormat.Norm11 => splatCount,
                SHFormat.Norm6 => splatCount,
                SHFormat.Cluster64k => 64 * 1024,
                SHFormat.Cluster32k => 32 * 1024,
                SHFormat.Cluster16k => 16 * 1024,
                SHFormat.Cluster8k => 8 * 1024,
                SHFormat.Cluster4k => 4 * 1024,
                _ => throw new ArgumentOutOfRangeException(nameof(fmt), fmt, null)
            };
        }

        public static (int,int) CalcTextureSize(int splatCount)
        {
            int width = TextureWidth;
            int height = math.max(1, (splatCount + width - 1) / width);
            // our swizzle tiles are 16x16, so make texture multiple of that height
            int blockHeight = 16;
            height = (height + blockHeight - 1) / blockHeight * blockHeight;
            return (width, height);
        }

        public static GraphicsFormat ColorFormatToGraphics(ColorFormat format)
        {
            return format switch
            {
                ColorFormat.Float32x4 => GraphicsFormat.R32G32B32A32_SFloat,
                ColorFormat.Float16x4 => GraphicsFormat.R16G16B16A16_SFloat,
                ColorFormat.Norm8x4 => GraphicsFormat.R8G8B8A8_UNorm,
                ColorFormat.BC7 => GraphicsFormat.RGBA_BC7_UNorm,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };
        }

        public static long CalcPosDataSize(int splatCount, VectorFormat formatPos)
        {
            return (long)splatCount * GetVectorSize(formatPos);
        }
        public static long CalcOtherDataSize(int splatCount, VectorFormat formatScale, SHFormat formatSh)
        {
            return (long)splatCount * GetOtherSize(formatScale, formatSh);
        }
        public static long CalcColorDataSize(int splatCount, ColorFormat formatColor)
        {
            var (width, height) = CalcTextureSize(splatCount);
            return (long)width * height * GetColorSize(formatColor);
        }
        public static long CalcSHDataSize(int splatCount, SHFormat formatSh, int degreeSh)
        {
            // long math: 11M+ splats at Float32 overflow an int. Cluster formats
            // store only the table here; their per-splat index lives in the
            // other data.
            return (long)GetSHCount(formatSh, splatCount) * GetSHStride(formatSh, degreeSh);
        }
        public static long CalcChunkDataSize(int splatCount)
        {
            long chunkCount = (splatCount + ChunkSize - 1) / ChunkSize;
            return chunkCount * UnsafeUtility.SizeOf<ChunkInfo>();
        }

        [FormerlySerializedAs("m_PosFormat"), FormerlySerializedAs("_posFormat")]
        public VectorFormat posFormat = VectorFormat.Norm11;
        [FormerlySerializedAs("m_ScaleFormat"), FormerlySerializedAs("_scaleFormat")]
        public VectorFormat scaleFormat = VectorFormat.Norm11;
        [FormerlySerializedAs("m_SHFormat"), FormerlySerializedAs("_shFormat")]
        public SHFormat shFormat = SHFormat.Norm11;
        [FormerlySerializedAs("m_ColorFormat"), FormerlySerializedAs("_colorFormat")]
        public ColorFormat colorFormat;

        [FormerlySerializedAs("m_PosData"), FormerlySerializedAs("_posData")]
        public TextAsset posData;
        [FormerlySerializedAs("m_ColorData"), FormerlySerializedAs("_colorData")]
        public TextAsset colorData;
        [FormerlySerializedAs("m_OtherData"), FormerlySerializedAs("_otherData")]
        public TextAsset otherData;
        // Null when shDegree is 0
        [FormerlySerializedAs("m_SHData"), FormerlySerializedAs("_shData")]
        public TextAsset shData;
        // Chunk data is optional (if data formats are fully lossless then there's no chunking)
        [FormerlySerializedAs("m_ChunkData"), FormerlySerializedAs("_chunkData")]
        public TextAsset chunkData;

        [FormerlySerializedAs("m_Cameras"), FormerlySerializedAs("_cameras")]
        public CameraInfo[] cameras;

        public struct ChunkInfo
        {
            public uint colR, colG, colB, colA;
            public float2 posX, posY, posZ;
            public uint sclX, sclY, sclZ;
            public uint shR, shG, shB;
        }

        [Serializable]
        public struct CameraInfo
        {
            public Vector3 pos;
            public Vector3 axisX, axisY, axisZ;
            public float fov;
        }
    }
}
