using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// A clip on a <see cref="GaussianSubTimelineTrack"/>: plays the bound
    /// player's timeline, starting <see cref="ClipIn"/> seconds into it.
    /// </summary>
    [Serializable]
    public class GaussianSubTimelineClip : GaussianTimelineClip
    {
        [Tooltip("Where in the sub-timeline the clip starts, in seconds")]
        [SerializeField] private double clipIn;

        [Tooltip("Sub-timeline seconds per second of this clip")]
        [Min(0f)]
        [SerializeField] private float timeScale = 1f;

        public double ClipIn
        {
            get => clipIn;
            set => clipIn = Math.Max(0.0, value);
        }

        public float TimeScale
        {
            get => timeScale;
            set => timeScale = Mathf.Max(0f, value);
        }

        public double SubTime(double localTime)
        {
            return clipIn + localTime * timeScale;
        }
    }
}
