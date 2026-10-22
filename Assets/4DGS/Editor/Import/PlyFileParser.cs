// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Collections;

namespace GaussianSplatting.Editor.Import
{
    public static class PlyFileParser
    {
        public static void ReadFileHeader(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs)
        {
            vertexCount = 0;
            vertexStride = 0;
            attrs = new List<(string, ElementType)>();
            if (!File.Exists(filePath))
                return;
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, out _, fs);
        }

        // Parses the header up to end_header. Properties are scoped to their
        // element: only the vertex element's describe the splat records, and
        // any elements before it shift where the vertex data starts. Types or
        // layouts this reader cannot step over are rejected rather than
        // silently misaligning every attribute after them.
        static void ReadHeaderImpl(string filePath, out int vertexCount, out int vertexStride,
            out List<(string, ElementType)> attrs, out long vertexDataOffset, FileStream fs)
        {
            // C# arrays and NativeArrays make it hard to have a "byte" array larger than 2GB :/
            if (fs.Length >= 2 * 1024 * 1024 * 1024L)
                throw new IOException($"PLY {filePath} read error: currently files larger than 2GB are not supported");

            vertexCount = 0;
            vertexStride = 0;
            vertexDataOffset = 0;
            attrs = new List<(string, ElementType)>();
            const int kMaxHeaderLines = 9000;
            bool gotBinaryLE = false;
            bool inVertex = false;
            bool vertexSeen = false;
            long elementCount = 0;
            int elementStride = 0;
            bool elementHasList = false;
            string elementName = null;
            long bytesBeforeVertex = 0;

            void CloseElement()
            {
                if (elementName == null || vertexSeen)
                    return;
                if (elementHasList)
                    throw new IOException($"PLY {filePath} not supported: element '{elementName}' with list properties precedes the vertex data");
                bytesBeforeVertex += elementCount * elementStride;
            }

            for (int lineIdx = 0; lineIdx < kMaxHeaderLines; ++lineIdx)
            {
                var line = ReadLine(fs);
                if (line == "end_header" || line.Length == 0)
                    break;
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 0)
                    continue;
                if (tokens.Length == 3 && tokens[0] == "format" && tokens[1] == "binary_little_endian" && tokens[2] == "1.0")
                    gotBinaryLE = true;
                if (tokens[0] == "element" && tokens.Length == 3)
                {
                    if (inVertex)
                        vertexSeen = true;
                    CloseElement();
                    elementName = tokens[1];
                    elementCount = long.Parse(tokens[2]);
                    elementStride = 0;
                    elementHasList = false;
                    inVertex = elementName == "vertex";
                    if (inVertex)
                        vertexCount = (int)elementCount;
                }
                if (tokens[0] == "property")
                {
                    if (tokens.Length >= 2 && tokens[1] == "list")
                    {
                        if (inVertex)
                            throw new IOException($"PLY {filePath} not supported: list property in the vertex element");
                        elementHasList = true;
                        continue;
                    }
                    if (tokens.Length != 3)
                        continue;
                    ElementType type = ParseType(tokens[1]);
                    if (type == ElementType.None)
                        throw new IOException($"PLY {filePath} not supported: unknown property type '{tokens[1]}'");
                    elementStride += TypeToSize(type);
                    if (inVertex)
                    {
                        vertexStride += TypeToSize(type);
                        attrs.Add((tokens[2], type));
                    }
                }
            }

            if (!gotBinaryLE)
            {
                throw new IOException($"PLY {filePath} not supported: needs to be binary, little endian PLY format");
            }
            vertexDataOffset = fs.Position + bytesBeforeVertex;
        }

        static ElementType ParseType(string name)
        {
            return name switch
            {
                "float" or "float32" => ElementType.Float,
                "double" or "float64" => ElementType.Double,
                "uchar" or "uint8" => ElementType.UChar,
                "char" or "int8" => ElementType.Char,
                "short" or "int16" => ElementType.Short,
                "ushort" or "uint16" => ElementType.UShort,
                "int" or "int32" => ElementType.Int,
                "uint" or "uint32" => ElementType.UInt,
                _ => ElementType.None
            };
        }

        public static void ReadFile(string filePath, out int vertexCount, out int vertexStride, out List<(string, ElementType)> attrs, out NativeArray<byte> vertices)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            ReadHeaderImpl(filePath, out vertexCount, out vertexStride, out attrs, out long vertexDataOffset, fs);
            fs.Position = vertexDataOffset;

            vertices = new NativeArray<byte>(vertexCount * vertexStride, Allocator.Persistent);
            int expectedBytes = vertices.Length;
            var readBytes = fs.Read(vertices);
            if (readBytes != expectedBytes)
            {
                vertices.Dispose();
                throw new IOException($"PLY {filePath} read error, expected {expectedBytes} data bytes got {readBytes}");
            }
        }

        public enum ElementType
        {
            None,
            Float,
            Double,
            UChar,
            Char,
            Short,
            UShort,
            Int,
            UInt
        }

        public static int TypeToSize(ElementType t)
        {
            return t switch
            {
                ElementType.None => 0,
                ElementType.Float => 4,
                ElementType.Double => 8,
                ElementType.UChar => 1,
                ElementType.Char => 1,
                ElementType.Short => 2,
                ElementType.UShort => 2,
                ElementType.Int => 4,
                ElementType.UInt => 4,
                _ => throw new ArgumentOutOfRangeException(nameof(t), t, null)
            };
        }

        static string ReadLine(FileStream fs)
        {
            var byteBuffer = new List<byte>();
            while (true)
            {
                int b = fs.ReadByte();
                if (b == -1 || b == '\n')
                    break;
                byteBuffer.Add((byte)b);
            }
            // if line had CRLF line endings, remove the CR part
            if (byteBuffer.Count > 0 && byteBuffer.Last() == '\r')
                byteBuffer.RemoveAt(byteBuffer.Count-1);
            return Encoding.UTF8.GetString(byteBuffer.ToArray());
        }
    }
}
