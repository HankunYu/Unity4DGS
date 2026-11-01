using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>
    /// A clip on a <see cref="TideAnimationTrack"/>: plays an animation
    /// clip on the bound Animator from a time at a speed, looping or holding
    /// its last frame.
    /// </summary>
    [Serializable]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianAnimationClip")]
    public class TideAnimationClip : TideClip
    {
        [SerializeField] private AnimationClip animation;

        [Tooltip("Animation time at the start of the clip, in seconds")]
        [Min(0f)]
        [SerializeField] private double clipIn;

        [Tooltip("Animation seconds per timeline second")]
        [Min(0f)]
        [SerializeField] private float speed = 1f;

        [Tooltip("Repeat the animation past its end instead of holding the last frame")]
        [SerializeField] private bool loop = true;

        public AnimationClip Animation
        {
            get => animation;
            set => animation = value;
        }

        public double ClipIn
        {
            get => clipIn;
            set => clipIn = Math.Max(0.0, value);
        }

        public float Speed
        {
            get => speed;
            set => speed = Mathf.Max(0f, value);
        }

        public bool Loop
        {
            get => loop;
            set => loop = value;
        }

        public override string DisplayName =>
            !string.IsNullOrEmpty(Name) ? Name : animation != null ? animation.name : "Animation";

        public double AnimationTime(double localTime)
        {
            double time = clipIn + localTime * speed;
            if (animation == null || animation.length <= 0f)
                return time;
            double length = animation.length;
            return loop ? time - Math.Floor(time / length) * length : Math.Min(time, length);
        }
    }
}
