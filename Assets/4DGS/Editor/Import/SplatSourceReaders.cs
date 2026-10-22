using System;
using System.IO;

namespace GaussianSplatting.Editor.Import
{
    // Picks the reader for a source file by its extension.
    public static class SplatSourceReaders
    {
        public static bool IsSupported(string filePath) => Find(filePath) != null;

        public static ISplatSourceReader ForFile(string filePath)
        {
            return Find(filePath) ?? throw new IOException($"File {filePath} is not a supported splat format");
        }

        static ISplatSourceReader Find(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            if (ext.Equals(".ply", StringComparison.OrdinalIgnoreCase))
                return new PlySourceReader();
            if (ext.Equals(".spz", StringComparison.OrdinalIgnoreCase))
                return new SpzSourceReader();
            return null;
        }
    }
}
