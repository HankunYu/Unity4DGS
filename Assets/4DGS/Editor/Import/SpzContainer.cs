using System;
using System.IO;
using System.IO.Compression;
using Unity.Collections;
using ZstdSharp;

namespace GaussianSplatting.Editor.Import
{
    // Reads and writes the SPZ file containers (see SpzFormat): gzip for
    // versions 1-3, header + table of contents + zstd streams for 4.
    public static class SpzContainer
    {
        // Rejects headers that would overflow the stream sizes or are not
        // plausibly real; far above anything the renderer accepts anyway.
        const int kMaxPoints = 100_000_000;
        const int kZstdLevel = 12; // reference encoder default

        // Header fields only; streams are not allocated.
        public static SpzPackedData ReadHeader(string filePath)
        {
            using var fs = File.OpenRead(filePath);
            var head = new byte[SpzFormat.HeaderSize];
            int got = ReadUpTo(fs, head, 0, head.Length);
            if (got >= 4 && BitConverter.ToUInt32(head, 0) == SpzFormat.Magic)
            {
                if (got < SpzFormat.HeaderSize)
                    throw new IOException($"SPZ {filePath}: file too short");
                return ParseNgspHeader(filePath, head, out _, out _, out _);
            }
            if (got >= 2 && head[0] == 0x1f && head[1] == 0x8b)
            {
                fs.Position = 0;
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                var legacy = new byte[SpzFormat.LegacyHeaderSize];
                if (ReadUpTo(gz, legacy, 0, legacy.Length) != legacy.Length)
                    throw new IOException($"SPZ {filePath}: failed to read header");
                return ParseLegacyHeader(filePath, legacy, out _);
            }
            throw new IOException($"SPZ {filePath}: unrecognized format");
        }

        public static SpzPackedData Read(string filePath)
        {
            byte[] file = File.ReadAllBytes(filePath);
            if (file.Length >= 4 && BitConverter.ToUInt32(file, 0) == SpzFormat.Magic)
                return ReadNgsp(filePath, file);
            if (file.Length >= 2 && file[0] == 0x1f && file[1] == 0x8b)
            {
                using var gz = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
                using var raw = new MemoryStream();
                gz.CopyTo(raw);
                return ReadLegacy(filePath, raw.GetBuffer(), (int)raw.Length);
            }
            throw new IOException($"SPZ {filePath}: unrecognized format");
        }

        public static void Write(string filePath, SpzPackedData data)
        {
            if (data.version < SpzFormat.MinZstdVersion)
                WriteLegacy(filePath, data);
            else
                WriteNgsp(filePath, data);
        }

        static SpzPackedData ParseNgspHeader(string filePath, byte[] h, out int numStreams, out int tocOffset, out bool hasExtensions)
        {
            var data = new SpzPackedData
            {
                version = (int)BitConverter.ToUInt32(h, 4),
                numPoints = (int)Math.Min(BitConverter.ToUInt32(h, 8), int.MaxValue),
                shDegree = h[12],
                fractionalBits = h[13],
                antialiased = (h[14] & SpzFormat.FlagAntialiased) != 0,
            };
            hasExtensions = (h[14] & SpzFormat.FlagHasExtensions) != 0;
            numStreams = h[15];
            tocOffset = (int)Math.Min(BitConverter.ToUInt32(h, 16), int.MaxValue);
            if (data.version < SpzFormat.MinZstdVersion || data.version > SpzFormat.LatestVersion)
                throw new IOException($"SPZ {filePath}: unsupported version {data.version}");
            Validate(filePath, data);
            return data;
        }

        static SpzPackedData ParseLegacyHeader(string filePath, byte[] h, out bool hasExtensions)
        {
            if (BitConverter.ToUInt32(h, 0) != SpzFormat.Magic)
                throw new IOException($"SPZ {filePath}: header magic unexpected");
            var data = new SpzPackedData
            {
                version = (int)BitConverter.ToUInt32(h, 4),
                numPoints = (int)Math.Min(BitConverter.ToUInt32(h, 8), int.MaxValue),
                shDegree = h[12],
                fractionalBits = h[13],
                antialiased = (h[14] & SpzFormat.FlagAntialiased) != 0,
            };
            hasExtensions = (h[14] & SpzFormat.FlagHasExtensions) != 0;
            // version 1 used float16 positions and was never released
            if (data.version < 2 || data.version >= SpzFormat.MinZstdVersion)
                throw new IOException($"SPZ {filePath}: unsupported version {data.version}");
            Validate(filePath, data);
            return data;
        }

        static void Validate(string filePath, SpzPackedData data)
        {
            if (data.numPoints < 1 || data.numPoints > kMaxPoints)
                throw new IOException($"SPZ {filePath}: out of range splat count {data.numPoints}");
            if (data.shDegree > SpzFormat.MaxFileSHDegree)
                throw new IOException($"SPZ {filePath}: out of range SH degree {data.shDegree}");
            if (data.fractionalBits > 24)
                throw new IOException($"SPZ {filePath}: out of range fractional bits {data.fractionalBits}");
        }

        static SpzPackedData ReadLegacy(string filePath, byte[] raw, int length)
        {
            if (length < SpzFormat.LegacyHeaderSize)
                throw new IOException($"SPZ {filePath}: failed to read header");
            var header = new byte[SpzFormat.LegacyHeaderSize];
            Array.Copy(raw, header, header.Length);
            var data = ParseLegacyHeader(filePath, header, out _);
            // Trailing extension records, if any, are ignored.
            try
            {
                data.Allocate();
                int offset = SpzFormat.LegacyHeaderSize;
                foreach (var stream in data.Streams)
                {
                    if (offset + stream.Length > length)
                        throw new IOException($"SPZ {filePath}: file smaller than it should be");
                    NativeArray<byte>.Copy(raw, offset, stream, 0, stream.Length);
                    offset += stream.Length;
                }
                return data;
            }
            catch
            {
                data.Dispose();
                throw;
            }
        }

        static SpzPackedData ReadNgsp(string filePath, byte[] file)
        {
            if (file.Length < SpzFormat.HeaderSize)
                throw new IOException($"SPZ {filePath}: file too short");
            var data = ParseNgspHeader(filePath, file, out int numStreams, out int tocOffset, out _);
            try
            {
                data.Allocate();
                // Empty streams (sh at degree 0) are not stored.
                var streams = Array.FindAll(data.Streams, s => s.Length > 0);
                if (numStreams != streams.Length)
                    throw new IOException($"SPZ {filePath}: expected {streams.Length} streams, header says {numStreams}");
                long tocEnd = tocOffset + (long)numStreams * 16;
                if (tocOffset < SpzFormat.HeaderSize || tocEnd > file.Length)
                    throw new IOException($"SPZ {filePath}: table of contents out of range");

                long offset = tocEnd;
                using var decompressor = new Decompressor();
                for (int i = 0; i < numStreams; i++)
                {
                    long compressedSize = BitConverter.ToInt64(file, tocOffset + i * 16);
                    long uncompressedSize = BitConverter.ToInt64(file, tocOffset + i * 16 + 8);
                    if (uncompressedSize != streams[i].Length)
                        throw new IOException($"SPZ {filePath}: stream {i} size mismatch");
                    if (compressedSize < 0 || offset + compressedSize > file.Length)
                        throw new IOException($"SPZ {filePath}: stream {i} extends past the end of the file");
                    int written = decompressor.Unwrap(new ReadOnlySpan<byte>(file, (int)offset, (int)compressedSize), streams[i].AsSpan());
                    if (written != streams[i].Length)
                        throw new IOException($"SPZ {filePath}: stream {i} decompressed to {written} bytes, expected {streams[i].Length}");
                    offset += compressedSize;
                }
                return data;
            }
            catch
            {
                data.Dispose();
                throw;
            }
        }

        static void WriteLegacy(string filePath, SpzPackedData data)
        {
            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            using var w = new BinaryWriter(gz);
            w.Write(SpzFormat.Magic);
            w.Write((uint)data.version);
            w.Write((uint)data.numPoints);
            w.Write((byte)data.shDegree);
            w.Write((byte)data.fractionalBits);
            w.Write(data.antialiased ? SpzFormat.FlagAntialiased : (byte)0);
            w.Write((byte)0);
            foreach (var stream in data.Streams)
                w.Write(stream.AsReadOnlySpan());
        }

        static void WriteNgsp(string filePath, SpzPackedData data)
        {
            var streams = Array.FindAll(data.Streams, s => s.Length > 0);
            var compressed = new byte[streams.Length][];
            using (var compressor = new Compressor(kZstdLevel))
            {
                for (int i = 0; i < streams.Length; i++)
                    compressed[i] = compressor.Wrap(streams[i].AsReadOnlySpan()).ToArray();
            }

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
            using var w = new BinaryWriter(fs);
            w.Write(SpzFormat.Magic);
            w.Write((uint)data.version);
            w.Write((uint)data.numPoints);
            w.Write((byte)data.shDegree);
            w.Write((byte)data.fractionalBits);
            w.Write(data.antialiased ? SpzFormat.FlagAntialiased : (byte)0);
            w.Write((byte)streams.Length);
            w.Write((uint)SpzFormat.HeaderSize); // no extensions: TOC follows the header
            w.Write(new byte[12]);
            for (int i = 0; i < streams.Length; i++)
            {
                w.Write((ulong)compressed[i].Length);
                w.Write((ulong)streams[i].Length);
            }
            foreach (var c in compressed)
                w.Write(c);
        }

        static int ReadUpTo(Stream s, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = s.Read(buffer, offset + total, count - total);
                if (read <= 0)
                    break;
                total += read;
            }
            return total;
        }
    }
}
