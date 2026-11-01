namespace Tide
{
    /// <summary>
    /// Something that accumulates phases over time at rates it reports, e.g.
    /// a procedural animation running at an adjustable speed. Accumulated on
    /// its own, its phase at a time depends on everything that happened
    /// before; handed to a <see cref="ITidePhaseSource"/> (a timeline that
    /// integrated the rates over its whole length), it depends on the time
    /// alone, so seeking shows what playback would.
    /// </summary>
    public interface ITidePhaseTarget
    {
        /// <summary>How many phases the target accumulates.</summary>
        int PhaseCount { get; }

        /// <summary>
        /// Per-second growth of each phase, from the target's current state.
        /// <paramref name="rates"/> holds <see cref="PhaseCount"/> entries.
        /// </summary>
        void GetPhaseRates(float[] rates);

        /// <summary>The source that owns the phases, or null while the target accumulates them itself.</summary>
        ITidePhaseSource PhaseSource { get; }

        /// <summary>Hands the phases to <paramref name="source"/>, or back to accumulation with null.</summary>
        void SetPhaseSource(ITidePhaseSource source);
    }
}
