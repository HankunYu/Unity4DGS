namespace Tide
{
    /// <summary>
    /// Owns the phases of <see cref="ITidePhaseTarget"/>s instead of them
    /// accumulating, e.g. a timeline that knows the phase at any of its times.
    /// </summary>
    public interface ITidePhaseSource
    {
        /// <summary>Writes the target's current phases, or returns false to let it accumulate.</summary>
        bool TryGetPhases(ITidePhaseTarget target, double[] phases);
    }
}
