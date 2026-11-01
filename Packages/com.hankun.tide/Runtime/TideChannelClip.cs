using System;

namespace Tide
{
    /// <summary>A clip on a <see cref="TideChannelTrack"/>: one value for the channel per clip-local time.</summary>
    [Serializable]
    public abstract class TideChannelClip : TideClip
    {
        public abstract float Evaluate(double localTime);
    }
}
