using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// A clip on a <see cref="GaussianParticleTrack"/>: runs the bound
    /// emitter's clock from a time at a speed, so its particles depend on the
    /// timeline's time alone.
    /// </summary>
    [Serializable]
    public class GaussianParticleClip : GaussianTimelineClip
    {
        [Tooltip("Emitter time at the start of the clip; the particles are prewarmed to it")]
        [SerializeField] private double clipIn;

        [Tooltip("Emitter seconds per timeline second: below 1 slows the particles down")]
        [Min(0f)]
        [SerializeField] private float speed = 1f;

        public double ClipIn
        {
            get => clipIn;
            set => clipIn = value;
        }

        public float Speed
        {
            get => speed;
            set => speed = Mathf.Max(0f, value);
        }

        public override string DisplayName => string.IsNullOrEmpty(Name) ? "Particles" : Name;

        public double EmitterTime(double localTime) => clipIn + localTime * speed;
    }
}
