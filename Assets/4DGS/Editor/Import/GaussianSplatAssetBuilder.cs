// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GaussianSplatting.Editor.Import
{
    // Turns linear-space source splats into the encoded data blocks of a
    // GaussianSplatAsset. Pure transform: no file or AssetDatabase access,
    // so the importer and any other front end share one pipeline.
    [BurstCompile]
    public static class GaussianSplatAssetBuilder
    {
        // Reports a stage name and overall progress in 0..1.
        public delegate void ProgressCallback(string stage, float progress);

        // Builds the asset data from splats whose SH goes up to sourceSHDegree.
        // The array is reordered in place and may be replaced (pruning), so it
        // is passed by ref; the caller keeps owning whatever it holds
        // afterwards. Throws InvalidOperationException when the result would
        // be unusable.
        public static GaussianSplatBuildResult Build(ref NativeArray<InputSplatData> splats, int sourceSHDegree,
            GaussianSplatImportSettings settings, GaussianSplatAsset.CameraInfo[] cameras, ProgressCallback progress = null)
        {
            progress ??= (_, _) => { };
            int shDegree = settings.SHDegreeFor(sourceSHDegree);
            int shCoeffs = GaussianSplatAsset.GetSHCoeffCount(shDegree);
            GaussianSplatFormats formats = settings.Formats.ForSHDegree(shDegree);

            if (settings.pruneOpacity > 0f)
                splats = PruneTransparentSplats(splats, settings.pruneOpacity);
            if (splats.Length == 0)
                throw new InvalidOperationException("every splat is below the prune opacity");
            if (splats.Length > GaussianSplatAsset.MaxSplats)
                throw new InvalidOperationException($"{splats.Length} splats; at most {GaussianSplatAsset.MaxSplats} are supported");

            float3 boundsMin, boundsMax;
            unsafe
            {
                new CalcBoundsJob
                {
                    m_BoundsMin = &boundsMin,
                    m_BoundsMax = &boundsMax,
                    m_SplatData = splats
                }.Schedule().Complete();
            }

            progress("Morton reordering", 0.05f);
            ReorderMorton(splats, boundsMin, boundsMax);

            var result = new GaussianSplatBuildResult();
            NativeArray<int> splatSHIndices = default;
            NativeArray<float> clusteredSHs = default;
            try
            {
                if (formats.UsesSHClusters)
                {
                    progress("Cluster SHs", 0.2f);
                    ClusterSHs(splats, formats.sh, shCoeffs, val =>
                    {
                        progress($"Cluster SHs ({val:P0})", 0.2f + val * 0.5f);
                        return true;
                    }, out clusteredSHs, out splatSHIndices);
                }

                progress("Creating data objects", 0.7f);
                GaussianSplatAsset asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
                asset.Initialize(splats.Length, formats.pos, formats.scale, formats.color, formats.sh, shDegree, boundsMin, boundsMax, cameras);
                result.asset = asset;

                var dataHash = new Hash128((uint)asset.splatCount, (uint)asset.formatVersion, (uint)shDegree, 0);
                // Chunking normalizes the splats in place, so it must run
                // before the per-attribute encoders.
                if (formats.UsesChunks)
                    result.chunkData = CreateChunkData(splats, ref dataHash, formats.sh, shCoeffs);
                result.posData = CreatePositionsData(splats, ref dataHash, formats.pos);
                result.otherData = CreateOtherData(splats, ref dataHash, formats.scale, splatSHIndices);
                result.colorData = CreateColorData(splats, ref dataHash, formats.color);
                if (shCoeffs > 0)
                    result.shData = CreateSHData(splats, ref dataHash, formats.sh, shDegree, clusteredSHs);
                asset.SetDataHash(dataHash);
                return result;
            }
            catch
            {
                if (result.asset != null)
                    UnityEngine.Object.DestroyImmediate(result.asset);
                result.Dispose();
                throw;
            }
            finally
            {
                if (splatSHIndices.IsCreated)
                    splatSHIndices.Dispose();
                if (clusteredSHs.IsCreated)
                    clusteredSHs.Dispose();
            }
        }

        // Returns the splats at or above minOpacity, disposing the input when
        // it had to be replaced.
        static NativeArray<InputSplatData> PruneTransparentSplats(NativeArray<InputSplatData> splats, float minOpacity)
        {
            int kept = 0;
            for (int i = 0; i < splats.Length; ++i)
                kept += splats[i].opacity >= minOpacity ? 1 : 0;
            if (kept == splats.Length)
                return splats;

            var result = new NativeArray<InputSplatData>(kept, Allocator.Persistent);
            for (int i = 0, o = 0; i < splats.Length; ++i)
            {
                if (splats[i].opacity >= minOpacity)
                    result[o++] = splats[i];
            }
            Debug.Log($"GS: pruned {splats.Length - kept:N0} of {splats.Length:N0} splats below opacity {minOpacity:0.###}");
            splats.Dispose();
            return result;
        }

        [BurstCompile]
        struct CalcBoundsJob : IJob
        {
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMin;
            [NativeDisableUnsafePtrRestriction] public unsafe float3* m_BoundsMax;
            [ReadOnly] public NativeArray<InputSplatData> m_SplatData;

            public unsafe void Execute()
            {
                float3 boundsMin = float.PositiveInfinity;
                float3 boundsMax = float.NegativeInfinity;

                for (int i = 0; i < m_SplatData.Length; ++i)
                {
                    float3 pos = m_SplatData[i].pos;
                    boundsMin = math.min(boundsMin, pos);
                    boundsMax = math.max(boundsMax, pos);
                }
                *m_BoundsMin = boundsMin;
                *m_BoundsMax = boundsMax;
            }
        }

        [BurstCompile]
        struct ReorderMortonJob : IJobParallelFor
        {
            const float kScaler = (float) ((1 << 21) - 1);
            public float3 m_BoundsMin;
            public float3 m_InvBoundsSize;
            [ReadOnly] public NativeArray<InputSplatData> m_SplatData;
            public NativeArray<(ulong,int)> m_Order;

            public void Execute(int index)
            {
                float3 pos = ((float3)m_SplatData[index].pos - m_BoundsMin) * m_InvBoundsSize * kScaler;
                uint3 ipos = (uint3) pos;
                ulong code = GaussianUtils.MortonEncode3(ipos);
                m_Order[index] = (code, index);
            }
        }

        struct OrderComparer : IComparer<(ulong, int)> {
            public int Compare((ulong, int) a, (ulong, int) b)
            {
                if (a.Item1 < b.Item1) return -1;
                if (a.Item1 > b.Item1) return +1;
                return a.Item2 - b.Item2;
            }
        }

        static void ReorderMorton(NativeArray<InputSplatData> splatData, float3 boundsMin, float3 boundsMax)
        {
            ReorderMortonJob order = new ReorderMortonJob
            {
                m_SplatData = splatData,
                m_BoundsMin = boundsMin,
                m_InvBoundsSize = 1.0f / (boundsMax - boundsMin),
                m_Order = new NativeArray<(ulong, int)>(splatData.Length, Allocator.TempJob)
            };
            order.Schedule(splatData.Length, 4096).Complete();
            order.m_Order.Sort(new OrderComparer());

            NativeArray<InputSplatData> copy = new(order.m_SplatData, Allocator.TempJob);
            for (int i = 0; i < copy.Length; ++i)
                order.m_SplatData[i] = copy[order.m_Order[i].Item2];
            copy.Dispose();

            order.m_Order.Dispose();
        }

        // First 'coeffs' SH coefficients of each splat, back to back
        [BurstCompile]
        static unsafe void GatherSHs(int splatCount, InputSplatData* splatData, int coeffs, float* shData)
        {
            for (int i = 0; i < splatCount; ++i)
            {
                UnsafeUtility.MemCpy(shData, SHCoeffs(splatData), coeffs * 3 * sizeof(float));
                splatData++;
                shData += coeffs * 3;
            }
        }

        static unsafe float3* SHCoeffs(InputSplatData* splat)
        {
            return (float3*)UnsafeUtility.AddressOf(ref splat->sh1);
        }

        // Outputs the cluster table, coeffs float3 per entry, and each
        // splat's entry.
        static unsafe void ClusterSHs(NativeArray<InputSplatData> splatData, GaussianSplatAsset.SHFormat format, int coeffs, Func<float, bool> progress, out NativeArray<float> shs, out NativeArray<int> shIndices)
        {
            int shDim = coeffs * 3;
            int shCount = GaussianSplatAsset.GetSHCount(format, splatData.Length);
            if (shCount >= splatData.Length)
            {
                // Fewer splats than clusters: no need to cluster, but the asset
                // is still a cluster format — the decoder reads a per-splat
                // table index from the other data — so emit a table with one
                // entry per splat and identity indices.
                shs = new NativeArray<float>(splatData.Length * shDim, Allocator.Persistent);
                GatherSHs(splatData.Length, (InputSplatData*) splatData.GetUnsafeReadOnlyPtr(), coeffs, (float*) shs.GetUnsafePtr());
                shIndices = new NativeArray<int>(splatData.Length, Allocator.Persistent);
                for (int i = 0; i < shIndices.Length; ++i)
                    shIndices[i] = i;
                return;
            }

            const int kBatchSize = 2048;
            float passesOverData = format switch
            {
                GaussianSplatAsset.SHFormat.Cluster64k => 0.3f,
                GaussianSplatAsset.SHFormat.Cluster32k => 0.4f,
                GaussianSplatAsset.SHFormat.Cluster16k => 0.5f,
                GaussianSplatAsset.SHFormat.Cluster8k => 0.8f,
                GaussianSplatAsset.SHFormat.Cluster4k => 1.2f,
                _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
            };

            float t0 = Time.realtimeSinceStartup;
            NativeArray<float> shData = new(splatData.Length * shDim, Allocator.Persistent);
            GatherSHs(splatData.Length, (InputSplatData*) splatData.GetUnsafeReadOnlyPtr(), coeffs, (float*) shData.GetUnsafePtr());

            shs = new(shCount * shDim, Allocator.Persistent);
            shIndices = new(splatData.Length, Allocator.Persistent);

            KMeansClustering.Calculate(shDim, shData, kBatchSize, passesOverData, progress, shs, shIndices);
            shData.Dispose();
            float t1 = Time.realtimeSinceStartup;
            Debug.Log($"GS: clustered {splatData.Length/1000000.0:F2}M SHs into {shCount/1024}K ({passesOverData:F1}pass/{kBatchSize}batch) in {t1-t0:F0}s");
        }

        [BurstCompile]
        struct CalcChunkDataJob : IJobParallelFor
        {
            [NativeDisableParallelForRestriction] public NativeArray<InputSplatData> splatData;
            public NativeArray<GaussianSplatAsset.ChunkInfo> chunks;
            // The runtime decoder only de-normalizes chunk-relative SH for
            // Float16/Norm11/Norm6; Float32 SH is read raw, and cluster formats
            // store SH in a separate table — normalizing those here would bake
            // permanently corrupted SH into the asset.
            public bool normalizeSh;
            // Only these SH coefficients are stored; the rest must not widen
            // the bounds
            public int shCoeffs;

            public unsafe void Execute(int chunkIdx)
            {
                float3 chunkMinpos = float.PositiveInfinity;
                float3 chunkMinscl = float.PositiveInfinity;
                float4 chunkMincol = float.PositiveInfinity;
                float3 chunkMinshs = float.PositiveInfinity;
                float3 chunkMaxpos = float.NegativeInfinity;
                float3 chunkMaxscl = float.NegativeInfinity;
                float4 chunkMaxcol = float.NegativeInfinity;
                float3 chunkMaxshs = float.NegativeInfinity;

                int splatBegin = math.min(chunkIdx * GaussianSplatAsset.ChunkSize, splatData.Length);
                int splatEnd = math.min((chunkIdx + 1) * GaussianSplatAsset.ChunkSize, splatData.Length);

                // calculate data bounds inside the chunk
                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];

                    // transform scale to be more uniformly distributed
                    s.scale = math.pow(s.scale, 1.0f / 8.0f);
                    // transform opacity to be more unformly distributed
                    s.opacity = GaussianUtils.SquareCentered01(s.opacity);
                    splatData[i] = s;

                    chunkMinpos = math.min(chunkMinpos, s.pos);
                    chunkMinscl = math.min(chunkMinscl, s.scale);
                    chunkMincol = math.min(chunkMincol, new float4(s.dc0, s.opacity));

                    chunkMaxpos = math.max(chunkMaxpos, s.pos);
                    chunkMaxscl = math.max(chunkMaxscl, s.scale);
                    chunkMaxcol = math.max(chunkMaxcol, new float4(s.dc0, s.opacity));
                    float3* sh = SHCoeffs(&s);
                    for (int k = 0; k < shCoeffs; ++k)
                    {
                        chunkMinshs = math.min(chunkMinshs, sh[k]);
                        chunkMaxshs = math.max(chunkMaxshs, sh[k]);
                    }
                }

                // make sure bounds are not zero
                chunkMaxpos = math.max(chunkMaxpos, chunkMinpos + 1.0e-5f);
                chunkMaxscl = math.max(chunkMaxscl, chunkMinscl + 1.0e-5f);
                chunkMaxcol = math.max(chunkMaxcol, chunkMincol + 1.0e-5f);
                chunkMaxshs = math.max(chunkMaxshs, chunkMinshs + 1.0e-5f);

                // store chunk info
                GaussianSplatAsset.ChunkInfo info = default;
                info.posX = new float2(chunkMinpos.x, chunkMaxpos.x);
                info.posY = new float2(chunkMinpos.y, chunkMaxpos.y);
                info.posZ = new float2(chunkMinpos.z, chunkMaxpos.z);
                info.sclX = math.f32tof16(chunkMinscl.x) | (math.f32tof16(chunkMaxscl.x) << 16);
                info.sclY = math.f32tof16(chunkMinscl.y) | (math.f32tof16(chunkMaxscl.y) << 16);
                info.sclZ = math.f32tof16(chunkMinscl.z) | (math.f32tof16(chunkMaxscl.z) << 16);
                info.colR = math.f32tof16(chunkMincol.x) | (math.f32tof16(chunkMaxcol.x) << 16);
                info.colG = math.f32tof16(chunkMincol.y) | (math.f32tof16(chunkMaxcol.y) << 16);
                info.colB = math.f32tof16(chunkMincol.z) | (math.f32tof16(chunkMaxcol.z) << 16);
                info.colA = math.f32tof16(chunkMincol.w) | (math.f32tof16(chunkMaxcol.w) << 16);
                info.shR = math.f32tof16(chunkMinshs.x) | (math.f32tof16(chunkMaxshs.x) << 16);
                info.shG = math.f32tof16(chunkMinshs.y) | (math.f32tof16(chunkMaxshs.y) << 16);
                info.shB = math.f32tof16(chunkMinshs.z) | (math.f32tof16(chunkMaxshs.z) << 16);
                chunks[chunkIdx] = info;

                // adjust data to be 0..1 within chunk bounds
                for (int i = splatBegin; i < splatEnd; ++i)
                {
                    InputSplatData s = splatData[i];
                    s.pos = ((float3)s.pos - chunkMinpos) / (chunkMaxpos - chunkMinpos);
                    s.scale = ((float3)s.scale - chunkMinscl) / (chunkMaxscl - chunkMinscl);
                    s.dc0 = ((float3)s.dc0 - chunkMincol.xyz) / (chunkMaxcol.xyz - chunkMincol.xyz);
                    s.opacity = (s.opacity - chunkMincol.w) / (chunkMaxcol.w - chunkMincol.w);
                    if (normalizeSh)
                    {
                        float3* sh = SHCoeffs(&s);
                        for (int k = 0; k < shCoeffs; ++k)
                            sh[k] = (sh[k] - chunkMinshs) / (chunkMaxshs - chunkMinshs);
                    }
                    splatData[i] = s;
                }
            }
        }

        static NativeArray<byte> CreateChunkData(NativeArray<InputSplatData> splatData, ref Hash128 dataHash, GaussianSplatAsset.SHFormat shFormat, int shCoeffs)
        {
            int chunkCount = (splatData.Length + GaussianSplatAsset.ChunkSize - 1) / GaussianSplatAsset.ChunkSize;
            CalcChunkDataJob job = new CalcChunkDataJob
            {
                splatData = splatData,
                chunks = new(chunkCount, Allocator.TempJob),
                normalizeSh = shFormat is GaussianSplatAsset.SHFormat.Float16
                    or GaussianSplatAsset.SHFormat.Norm11
                    or GaussianSplatAsset.SHFormat.Norm6,
                shCoeffs = shCoeffs,
            };

            job.Schedule(chunkCount, 8).Complete();

            // hash the chunk contents (the ref overload would hash the array struct, i.e. its pointer)
            dataHash.Append(job.chunks);

            var bytes = ToPersistentBytes(job.chunks);
            job.chunks.Dispose();
            return bytes;
        }

        [BurstCompile]
        struct ConvertColorJob : IJobParallelFor
        {
            public int width, height;
            [ReadOnly] public NativeArray<float4> inputData;
            [NativeDisableParallelForRestriction] public NativeArray<byte> outputData;
            public GaussianSplatAsset.ColorFormat format;
            public int formatBytesPerPixel;

            public unsafe void Execute(int y)
            {
                int srcIdx = y * width;
                byte* dstPtr = (byte*) outputData.GetUnsafePtr() + y * width * formatBytesPerPixel;
                for (int x = 0; x < width; ++x)
                {
                    float4 pix = inputData[srcIdx];

                    switch (format)
                    {
                        case GaussianSplatAsset.ColorFormat.Float32x4:
                        {
                            *(float4*) dstPtr = pix;
                        }
                            break;
                        case GaussianSplatAsset.ColorFormat.Float16x4:
                        {
                            half4 enc = new half4(pix);
                            *(half4*) dstPtr = enc;
                        }
                            break;
                        case GaussianSplatAsset.ColorFormat.Norm8x4:
                        {
                            pix = math.saturate(pix);
                            uint enc = (uint)(pix.x * 255.5f) | ((uint)(pix.y * 255.5f) << 8) | ((uint)(pix.z * 255.5f) << 16) | ((uint)(pix.w * 255.5f) << 24);
                            *(uint*) dstPtr = enc;
                        }
                            break;
                    }

                    srcIdx++;
                    dstPtr += formatBytesPerPixel;
                }
            }
        }

        static ulong EncodeFloat3ToNorm16(float3 v) // 48 bits: 16.16.16
        {
            return (ulong) (v.x * 65535.5f) | ((ulong) (v.y * 65535.5f) << 16) | ((ulong) (v.z * 65535.5f) << 32);
        }
        static uint EncodeFloat3ToNorm11(float3 v) // 32 bits: 11.10.11
        {
            return (uint) (v.x * 2047.5f) | ((uint) (v.y * 1023.5f) << 11) | ((uint) (v.z * 2047.5f) << 21);
        }
        static ushort EncodeFloat3ToNorm655(float3 v) // 16 bits: 6.5.5
        {
            return (ushort) ((uint) (v.x * 63.5f) | ((uint) (v.y * 31.5f) << 6) | ((uint) (v.z * 31.5f) << 11));
        }
        static ushort EncodeFloat3ToNorm565(float3 v) // 16 bits: 5.6.5
        {
            return (ushort) ((uint) (v.x * 31.5f) | ((uint) (v.y * 63.5f) << 5) | ((uint) (v.z * 31.5f) << 11));
        }

        static uint EncodeQuatToNorm10(float4 v) // 32 bits: 10.10.10.2
        {
            return (uint) (v.x * 1023.5f) | ((uint) (v.y * 1023.5f) << 10) | ((uint) (v.z * 1023.5f) << 20) | ((uint) (v.w * 3.5f) << 30);
        }

        static unsafe void EmitEncodedVector(float3 v, byte* outputPtr, GaussianSplatAsset.VectorFormat format)
        {
            switch (format)
            {
                case GaussianSplatAsset.VectorFormat.Float32:
                {
                    *(float*) outputPtr = v.x;
                    *(float*) (outputPtr + 4) = v.y;
                    *(float*) (outputPtr + 8) = v.z;
                }
                    break;
                case GaussianSplatAsset.VectorFormat.Norm16:
                {
                    ulong enc = EncodeFloat3ToNorm16(math.saturate(v));
                    *(uint*) outputPtr = (uint) enc;
                    *(ushort*) (outputPtr + 4) = (ushort) (enc >> 32);
                }
                    break;
                case GaussianSplatAsset.VectorFormat.Norm11:
                {
                    uint enc = EncodeFloat3ToNorm11(math.saturate(v));
                    *(uint*) outputPtr = enc;
                }
                    break;
                case GaussianSplatAsset.VectorFormat.Norm6:
                {
                    ushort enc = EncodeFloat3ToNorm655(math.saturate(v));
                    *(ushort*) outputPtr = enc;
                }
                    break;
            }
        }

        [BurstCompile]
        struct CreatePositionsDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            public GaussianSplatAsset.VectorFormat m_Format;
            public int m_FormatSize;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                byte* outputPtr = (byte*) m_Output.GetUnsafePtr() + index * m_FormatSize;
                EmitEncodedVector(m_Input[index].pos, outputPtr, m_Format);
            }
        }

        [BurstCompile]
        struct CreateOtherDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            [NativeDisableContainerSafetyRestriction] [ReadOnly] public NativeArray<int> m_SplatSHIndices;
            public GaussianSplatAsset.VectorFormat m_ScaleFormat;
            public int m_FormatSize;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public unsafe void Execute(int index)
            {
                byte* outputPtr = (byte*) m_Output.GetUnsafePtr() + index * m_FormatSize;

                // rotation: 4 bytes
                {
                    Quaternion rotQ = m_Input[index].rot;
                    float4 rot = new float4(rotQ.x, rotQ.y, rotQ.z, rotQ.w);
                    uint enc = EncodeQuatToNorm10(rot);
                    *(uint*) outputPtr = enc;
                    outputPtr += 4;
                }

                // scale: 6, 4 or 2 bytes
                EmitEncodedVector(m_Input[index].scale, outputPtr, m_ScaleFormat);
                outputPtr += GaussianSplatAsset.GetVectorSize(m_ScaleFormat);

                // SH index
                if (m_SplatSHIndices.IsCreated)
                    *(ushort*) outputPtr = (ushort)m_SplatSHIndices[index];
            }
        }

        static int NextMultipleOf(int size, int multipleOf)
        {
            return (size + multipleOf - 1) / multipleOf * multipleOf;
        }

        static NativeArray<byte> CreatePositionsData(NativeArray<InputSplatData> inputSplats, ref Hash128 dataHash, GaussianSplatAsset.VectorFormat formatPos)
        {
            int dataLen = inputSplats.Length * GaussianSplatAsset.GetVectorSize(formatPos);
            dataLen = NextMultipleOf(dataLen, 8); // serialized as ulong
            NativeArray<byte> data = new(dataLen, Allocator.Persistent);

            CreatePositionsDataJob job = new CreatePositionsDataJob
            {
                m_Input = inputSplats,
                m_Format = formatPos,
                m_FormatSize = GaussianSplatAsset.GetVectorSize(formatPos),
                m_Output = data
            };
            job.Schedule(inputSplats.Length, 8192).Complete();

            dataHash.Append(data);
            return data;
        }

        static NativeArray<byte> CreateOtherData(NativeArray<InputSplatData> inputSplats, ref Hash128 dataHash, GaussianSplatAsset.VectorFormat formatScale, NativeArray<int> splatSHIndices)
        {
            int formatSize = GaussianSplatAsset.GetOtherSizeNoSHIndex(formatScale);
            if (splatSHIndices.IsCreated)
                formatSize += 2;
            int dataLen = inputSplats.Length * formatSize;

            dataLen = NextMultipleOf(dataLen, 8); // serialized as ulong
            NativeArray<byte> data = new(dataLen, Allocator.Persistent);

            CreateOtherDataJob job = new CreateOtherDataJob
            {
                m_Input = inputSplats,
                m_SplatSHIndices = splatSHIndices,
                m_ScaleFormat = formatScale,
                m_FormatSize = formatSize,
                m_Output = data
            };
            job.Schedule(inputSplats.Length, 8192).Complete();

            dataHash.Append(data);
            return data;
        }

        static int SplatIndexToTextureIndex(uint idx)
        {
            uint2 xy = GaussianUtils.DecodeMorton2D_16x16(idx);
            uint width = GaussianSplatAsset.TextureWidth / 16;
            idx >>= 8;
            uint x = (idx % width) * 16 + xy.x;
            uint y = (idx / width) * 16 + xy.y;
            return (int)(y * GaussianSplatAsset.TextureWidth + x);
        }

        [BurstCompile]
        struct CreateColorDataJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<InputSplatData> m_Input;
            [NativeDisableParallelForRestriction] public NativeArray<float4> m_Output;

            public void Execute(int index)
            {
                var splat = m_Input[index];
                int i = SplatIndexToTextureIndex((uint)index);
                m_Output[i] = new float4(splat.dc0.x, splat.dc0.y, splat.dc0.z, splat.opacity);
            }
        }

        static NativeArray<byte> CreateColorData(NativeArray<InputSplatData> inputSplats, ref Hash128 dataHash, GaussianSplatAsset.ColorFormat formatColor)
        {
            var (width, height) = GaussianSplatAsset.CalcTextureSize(inputSplats.Length);
            NativeArray<float4> data = new(width * height, Allocator.TempJob);

            CreateColorDataJob job = new CreateColorDataJob();
            job.m_Input = inputSplats;
            job.m_Output = data;
            job.Schedule(inputSplats.Length, 8192).Complete();

            dataHash.Append(data);
            dataHash.Append((int)formatColor);

            GraphicsFormat gfxFormat = GaussianSplatAsset.ColorFormatToGraphics(formatColor);
            int dstSize = (int)GraphicsFormatUtility.ComputeMipmapSize(width, height, gfxFormat);

            NativeArray<byte> result;
            if (GraphicsFormatUtility.IsCompressedFormat(gfxFormat))
            {
                Texture2D tex = new Texture2D(width, height, GraphicsFormat.R32G32B32A32_SFloat, TextureCreationFlags.DontInitializePixels | TextureCreationFlags.DontUploadUponCreate);
                tex.SetPixelData(data, 0);
                EditorUtility.CompressTexture(tex, GraphicsFormatUtility.GetTextureFormat(gfxFormat), 100);
                // pixel data belongs to the texture; copy it out before destroying
                result = new NativeArray<byte>(tex.GetPixelData<byte>(0), Allocator.Persistent);
                UnityEngine.Object.DestroyImmediate(tex);
            }
            else
            {
                result = new NativeArray<byte>(dstSize, Allocator.Persistent);
                ConvertColorJob jobConvert = new ConvertColorJob
                {
                    width = width,
                    height = height,
                    inputData = data,
                    format = formatColor,
                    outputData = result,
                    formatBytesPerPixel = dstSize / width / height
                };
                jobConvert.Schedule(height, 1).Complete();
            }

            data.Dispose();
            return result;
        }

        // Packs SH entries back to back, GetSHStride bytes each
        [BurstCompile]
        unsafe struct CreateSHDataJob : IJobParallelFor
        {
            // Entry i's coefficients are float3s at m_Input + i * m_InputStride floats
            [NativeDisableUnsafePtrRestriction] public float* m_Input;
            public int m_InputStride;
            public int m_Coeffs;
            public GaussianSplatAsset.SHFormat m_Format;
            public int m_OutputStride;
            [NativeDisableParallelForRestriction] public NativeArray<byte> m_Output;

            public void Execute(int index)
            {
                float3* src = (float3*)(m_Input + (long)index * m_InputStride);
                byte* dst = (byte*)m_Output.GetUnsafePtr() + (long)index * m_OutputStride;
                for (int k = 0; k < m_Coeffs; ++k)
                {
                    switch (m_Format)
                    {
                        case GaussianSplatAsset.SHFormat.Float32:
                            ((float3*)dst)[k] = src[k];
                            break;
                        case GaussianSplatAsset.SHFormat.Norm11:
                            ((uint*)dst)[k] = EncodeFloat3ToNorm11(src[k]);
                            break;
                        case GaussianSplatAsset.SHFormat.Norm6:
                            ((ushort*)dst)[k] = EncodeFloat3ToNorm565(src[k]);
                            break;
                        default: // Float16, and the cluster tables
                            ((half3*)dst)[k] = new half3(src[k]);
                            break;
                    }
                }
            }
        }

        static NativeArray<byte> ToPersistentBytes<T>(NativeArray<T> data) where T : unmanaged
        {
            return new NativeArray<byte>(data.Reinterpret<byte>(UnsafeUtility.SizeOf<T>()), Allocator.Persistent);
        }

        // From the cluster table when there is one, else per splat
        static unsafe NativeArray<byte> CreateSHData(NativeArray<InputSplatData> inputSplats, ref Hash128 dataHash, GaussianSplatAsset.SHFormat formatSH, int shDegree, NativeArray<float> clusteredSHs)
        {
            int coeffs = GaussianSplatAsset.GetSHCoeffCount(shDegree);
            int entries = clusteredSHs.IsCreated ? clusteredSHs.Length / (coeffs * 3) : inputSplats.Length;
            int stride = GaussianSplatAsset.GetSHStride(formatSH, shDegree);
            NativeArray<byte> data = new(entries * stride, Allocator.Persistent);
            new CreateSHDataJob
            {
                m_Input = clusteredSHs.IsCreated
                    ? (float*)clusteredSHs.GetUnsafeReadOnlyPtr()
                    : (float*)SHCoeffs((InputSplatData*)inputSplats.GetUnsafeReadOnlyPtr()),
                m_InputStride = clusteredSHs.IsCreated ? coeffs * 3 : UnsafeUtility.SizeOf<InputSplatData>() / sizeof(float),
                m_Coeffs = coeffs,
                m_Format = formatSH,
                m_OutputStride = stride,
                m_Output = data,
            }.Schedule(entries, 8192).Complete();
            dataHash.Append(data);
            return data;
        }
    }
}
