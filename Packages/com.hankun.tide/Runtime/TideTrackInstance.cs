using System.Collections.Generic;

namespace Tide
{
    /// <summary>
    /// A track bound to its target for one player. It remembers the target's
    /// own values when created and puts them back in <see cref="Restore"/>, so
    /// the asset stays shared and the scene is left as it was found.
    /// </summary>
    public abstract class TideTrackInstance
    {
        public abstract void Evaluate(in TideEvaluation evaluation);

        public abstract void Restore();

        /// <summary>
        /// Adds the phase targets whose rates this track drives (e.g. a speed
        /// channel), so the player can tabulate their phases over time.
        /// Tracks that add any are evaluated at many times while the table is
        /// built, so they must not have side effects beyond their targets' values.
        /// </summary>
        public virtual void CollectPhaseTargets(List<ITidePhaseTarget> targets)
        {
        }
    }
}
