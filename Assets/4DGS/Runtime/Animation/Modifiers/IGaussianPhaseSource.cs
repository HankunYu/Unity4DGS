namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Owns a modifier's phases instead of the modifier accumulating them,
    /// e.g. a timeline that knows the phase at any of its times, so seeking
    /// shows the same animation playback would.
    /// </summary>
    public interface IGaussianPhaseSource
    {
        /// <summary>Writes the modifier's current phases, or returns false to let it accumulate.</summary>
        bool TryGetPhases(GaussianAnimModifier modifier, double[] phases);
    }
}
