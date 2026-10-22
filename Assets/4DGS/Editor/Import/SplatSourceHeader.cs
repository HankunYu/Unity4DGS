namespace GaussianSplatting.Editor.Import
{
    // What a source file declares about its splats, readable without
    // decoding the splat data itself.
    public struct SplatSourceHeader
    {
        public int splatCount;
        // 0..3; bands above this are absent from the file and read as zero
        public int shDegree;
    }
}
