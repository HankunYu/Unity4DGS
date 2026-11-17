using System;
using UnityEngine;
using Object = UnityEngine.Object;
using Tide;
using UnityEngine.Scripting.APIUpdating;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Records the bound <see cref="SequenceCapture"/> (a 360 or a flat capture) over the timeline's
    /// clips in play mode: one capture session, a take per clip. Every frame
    /// of a clip is written, numbered by its time in the clip, so frame k of
    /// a take shows the timeline k frames after the clip's start. Between
    /// clips the timeline jumps to the next one, starting the capture's
    /// warmup frames before it so motion settles; after the last one the
    /// session ends. Does nothing in edit mode.
    /// </summary>
    [Serializable]
    [TideDisplay("Capture", 0.85f, 0.30f, 0.30f)]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "Stereo360CaptureTrack")]
    public class SequenceCaptureTrack : TideClipTrack
    {
        [Tooltip("Leave play mode once the last take is written (editor only), so a long recording can run unattended")]
        [SerializeField] private bool exitPlayModeWhenDone;

        public override Type BindingType => typeof(SequenceCapture);

        public override Type ClipType => typeof(SequenceCaptureClip);

        public bool ExitPlayModeWhenDone
        {
            get => exitPlayModeWhenDone;
            set => exitPlayModeWhenDone = value;
        }

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            return new Instance(this, player, (SequenceCapture)binding);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly SequenceCaptureTrack _track;
            private readonly TidePlayer _player;
            private readonly SequenceCapture _capture;
            private bool _started;
            private bool _done;
            private int _openClip = -1;
            private int _lastFrame = -1;
            private double _seekTo = double.NaN;

            public Instance(SequenceCaptureTrack track, TidePlayer player, SequenceCapture capture)
            {
                _track = track;
                _player = player;
                _capture = capture;
            }

            public override void Evaluate(in TideEvaluation evaluation)
            {
                if (_capture == null || _done || !Application.isPlaying)
                    return;
                double time = evaluation.Time;
                double frameRate = _capture.FrameRate;

                if (!_started)
                {
                    // Nothing left to record, or a capture started by hand
                    // runs: stay out of it
                    if (NextClip(time, frameRate, true) < 0 || _capture.IsCapturing)
                        return;
                    if (!_capture.BeginSession(this, OnEndOfFrame))
                    {
                        _done = true;
                        return;
                    }
                    _started = true;
                }
                else if (!_capture.IsCapturing)
                {
                    // Stopped from outside (by hand, or it failed)
                    Finish(false);
                    return;
                }

                int clipIndex = ClipAt(time, frameRate, out int frame);
                if (clipIndex != _openClip)
                {
                    _capture.EndTake();
                    _openClip = clipIndex;
                    _lastFrame = -1;
                    if (clipIndex >= 0)
                    {
                        var clip = (SequenceCaptureClip)_track.Clips[clipIndex];
                        int every = _capture.CaptureEveryNthFrame;
                        _capture.BeginTake(clip.Name, (clip.FrameCount(frameRate) + every - 1) / every);
                    }
                }

                if (clipIndex >= 0)
                {
                    // Every Nth frame of the take, numbered without gaps; a
                    // frame shown twice (paused) is written once
                    int every = _capture.CaptureEveryNthFrame;
                    if (frame != _lastFrame && frame % every == 0)
                        _capture.WriteFrame(frame / every);
                    _lastFrame = frame;
                    return;
                }

                // Between takes: jump to the next one's warmup, unless it is
                // already that close, or finish after the last
                int next = NextClip(time, frameRate, false);
                if (next < 0)
                {
                    Finish(true);
                    return;
                }
                // At the end of this frame, so the next frame, a frame later,
                // is the first of the warmup
                double warmupStart = _track.Clips[next].Start - (_capture.WarmupFrames + 1) / frameRate;
                if (warmupStart > time + 0.5 / frameRate)
                    _seekTo = warmupStart;
            }

            public override void Restore()
            {
                if (_started && !_done && _capture != null && _capture.IsCapturing && _capture.IsDriven)
                    _capture.StopCapture();
                _done = true;
            }

            // Called by the capture at the end of each frame of the session:
            // jumping there, rather than while the player evaluates its
            // tracks, gives every track the new time
            private void OnEndOfFrame()
            {
                if (double.IsNaN(_seekTo))
                    return;
                double time = _seekTo;
                _seekTo = double.NaN;
                _player.Seek(time);
            }

            private void Finish(bool stopCapture)
            {
                _done = true;
                _openClip = -1;
                if (stopCapture && _capture.IsCapturing && _capture.IsDriven)
                    _capture.StopCapture();
#if UNITY_EDITOR
                if (_track.exitPlayModeWhenDone)
                    UnityEditor.EditorApplication.ExitPlaymode();
#endif
            }

            // The clip whose take holds the time, and the frame; -1 between
            // takes. The first one wins where clips overlap.
            private int ClipAt(double time, double frameRate, out int frame)
            {
                frame = -1;
                for (int i = 0; i < _track.Clips.Count; i++)
                {
                    if (_track.Clips[i] is SequenceCaptureClip clip && clip.TryGetFrame(time, frameRate, out frame))
                        return i;
                }
                frame = -1;
                return -1;
            }

            // The first clip whose take starts after the time, or also one
            // that holds it; -1 when none is left
            private int NextClip(double time, double frameRate, bool orCurrent)
            {
                for (int i = 0; i < _track.Clips.Count; i++)
                {
                    if (_track.Clips[i] is not SequenceCaptureClip clip)
                        continue;
                    if (orCurrent && clip.TryGetFrame(time, frameRate, out _))
                        return i;
                    if (clip.Start - 0.5 / frameRate > time)
                        return i;
                }
                return -1;
            }
        }
    }
}
