namespace GaussianSplatting.Runtime
{
    /// <summary>A clip that has a say at the evaluated time: its index on the track, weight and clip-local time.</summary>
    public readonly struct GaussianClipSample
    {
        public readonly int ClipIndex;
        public readonly float Weight;
        public readonly double LocalTime;

        public GaussianClipSample(int clipIndex, float weight, double localTime)
        {
            ClipIndex = clipIndex;
            Weight = weight;
            LocalTime = localTime;
        }
    }
}
