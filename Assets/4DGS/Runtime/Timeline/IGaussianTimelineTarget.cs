namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// A component a channel track can drive: named float channels, read and
    /// written one at a time. Channels holding discrete values (enums,
    /// toggles) snap in <see cref="BlendChannel"/> instead of interpolating.
    /// </summary>
    public interface IGaussianTimelineTarget
    {
        int ChannelCount { get; }

        string GetChannelName(int index);

        float GetChannelValue(int index);

        void SetChannelValue(int index, float value);

        /// <summary>The value <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
        float BlendChannel(int index, float from, float to, float t);
    }
}
