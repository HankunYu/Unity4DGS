using System;

namespace GaussianSplatting.Runtime
{
    /// <summary>A span during which a <see cref="GaussianActivationTrack"/> keeps its GameObject active.</summary>
    [Serializable]
    public class GaussianActivationClip : GaussianTimelineClip
    {
        public override string DisplayName => string.IsNullOrEmpty(Name) ? "Active" : Name;
    }
}
