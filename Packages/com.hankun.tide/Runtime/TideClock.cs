using UnityEngine;

namespace Tide
{
    /// <summary>
    /// The time for animated effects that no track drives to follow, e.g. a
    /// shader's time or procedural motion: read it instead of Time.time.
    /// While a <see cref="TidePlayer"/> that drives the scene clock is bound,
    /// it is that timeline's time, so a frame depends on the timeline alone
    /// and every take of a capture matches. Otherwise it is game time in play
    /// mode and editor time in edit mode.
    /// </summary>
    public static class TideClock
    {
        private static TidePlayer s_driver;
        private static double s_time;

        public static double Time
        {
            get
            {
                if (s_driver != null)
                    return s_time;
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    return UnityEditor.EditorApplication.timeSinceStartup;
#endif
                return UnityEngine.Time.timeAsDouble;
            }
        }

        /// <summary>The timeline player the clock follows, or null.</summary>
        public static TidePlayer Driver => s_driver;

        internal static void Drive(TidePlayer player, double time)
        {
            s_driver = player;
            s_time = time;
        }

        internal static void Release(TidePlayer player)
        {
            if (s_driver == player)
                s_driver = null;
        }
    }
}
