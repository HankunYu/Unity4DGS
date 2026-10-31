using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// A clip on a <see cref="GaussianSequenceTrack"/>: plays the bound
    /// player's 4D sequence from a frame at a speed. Loop extrapolation
    /// repeats the clip, so make the clip as long as the frames it should loop.
    /// </summary>
    [Serializable]
    public class GaussianSequenceClip : GaussianTimelineClip
    {
        [Min(0)]
        [SerializeField] private int startFrame;

        [Tooltip("Multiplies the sequence's frame rate")]
        [Min(0f)]
        [SerializeField] private float speed = 1f;

        public int StartFrame
        {
            get => startFrame;
            set => startFrame = Mathf.Max(0, value);
        }

        public float Speed
        {
            get => speed;
            set => speed = Mathf.Max(0f, value);
        }

        public override string DisplayName =>
            string.IsNullOrEmpty(Name) ? $"From frame {startFrame}" : Name;

        public int FrameAt(double localTime, GaussianSplatSequence sequence)
        {
            // The epsilon keeps a time that lands on a frame from rounding to the one before
            int frame = startFrame + (int)Math.Floor(localTime * sequence.frameRate * speed + 1e-6);
            return Mathf.Clamp(frame, 0, sequence.FrameCount - 1);
        }
    }
}
