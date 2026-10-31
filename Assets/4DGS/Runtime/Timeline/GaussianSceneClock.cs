using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// The time that animated effects follow: animation modifiers and styles
    /// (glints, flicker). While a <see cref="GaussianTimelinePlayer"/> that
    /// drives the scene clock is bound, it is that timeline's time, so a
    /// frame depends on the timeline alone and every capture take matches.
    /// Otherwise it is game time in play mode and editor time in edit mode.
    /// </summary>
    public static class GaussianSceneClock
    {
        private static GaussianTimelinePlayer s_driver;
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
        public static GaussianTimelinePlayer Driver => s_driver;

        internal static void Drive(GaussianTimelinePlayer player, double time)
        {
            s_driver = player;
            s_time = time;
        }

        internal static void Release(GaussianTimelinePlayer player)
        {
            if (s_driver == player)
                s_driver = null;
        }
    }
}
