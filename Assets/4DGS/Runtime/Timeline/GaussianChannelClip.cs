using System;

namespace GaussianSplatting.Runtime
{
    /// <summary>A clip on a <see cref="GaussianChannelTrack"/>: one value for the channel per clip-local time.</summary>
    [Serializable]
    public abstract class GaussianChannelClip : GaussianTimelineClip
    {
        public abstract float Evaluate(double localTime);
    }
}
