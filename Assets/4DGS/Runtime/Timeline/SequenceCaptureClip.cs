using System;
using UnityEngine;
using Tide;
using UnityEngine.Scripting.APIUpdating;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// A clip on a <see cref="SequenceCaptureTrack"/>: one take of the
    /// bound capture, from the clip's start to its end. Its frames are named
    /// after the clip (the capture's own prefix when it has no name) and
    /// numbered by their time in the clip.
    /// </summary>
    [Serializable]
    [TideDisplay("Capture")]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "Stereo360CaptureClip")]
    public class SequenceCaptureClip : TideClip
    {
        public override string DisplayName => string.IsNullOrEmpty(Name) ? "Take" : Name;

        /// <summary>Frames the take spans at this frame rate, written or not.</summary>
        public int FrameCount(double frameRate) => Math.Max(1, (int)Math.Round(Duration * frameRate));

        /// <summary>
        /// The frame of the take at a timeline time, on the frame rate's grid
        /// from the clip's start; false outside the take.
        /// </summary>
        public bool TryGetFrame(double time, double frameRate, out int frame)
        {
            frame = (int)Math.Round((time - Start) * frameRate);
            return frame >= 0 && frame < FrameCount(frameRate);
        }
    }
}
