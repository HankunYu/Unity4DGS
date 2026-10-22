using Unity.Collections;

namespace GaussianSplatting.Editor.Import
{
    // Decodes one splat file format into InputSplatData. Implementations
    // throw IOException for files they cannot read.
    public interface ISplatSourceReader
    {
        SplatSourceHeader ReadHeader(string filePath);

        // Returns linear-space splats (see InputSplatData) allocated with
        // Allocator.Persistent; the caller owns and disposes them.
        NativeArray<InputSplatData> Read(string filePath, out SplatSourceHeader header);
    }
}
