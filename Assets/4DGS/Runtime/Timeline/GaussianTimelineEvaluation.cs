namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// One evaluation of a timeline. <see cref="Continuous"/> is true when
    /// playback moved the time on from <see cref="PreviousTime"/> (so markers
    /// in between fire and audio keeps playing), false after a seek, a pause or
    /// an editor scrub. <see cref="Wrapped"/> means playback looped past the
    /// end on the way.
    /// </summary>
    public readonly struct GaussianTimelineEvaluation
    {
        public readonly double Time;
        public readonly double PreviousTime;
        public readonly double Duration;
        public readonly bool Continuous;
        public readonly bool Wrapped;

        public GaussianTimelineEvaluation(double time, double previousTime, double duration, bool continuous, bool wrapped)
        {
            Time = time;
            PreviousTime = previousTime;
            Duration = duration;
            Continuous = continuous;
            Wrapped = wrapped;
        }
    }
}
