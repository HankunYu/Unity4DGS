using System;
using GaussianSplatting.Runtime;
using Tide.Editor;
using UnityEditor;

namespace GaussianSplatting.Editor
{
    internal sealed class SequenceCaptureClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(SequenceCaptureClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            if (context.Binding is not SequenceCapture capture || context.Clip is not SequenceCaptureClip clip)
                return;
            int frames = clip.FrameCount(capture.FrameRate);
            int every = capture.CaptureEveryNthFrame;
            int written = (frames + every - 1) / every;
            string prefix = SequenceCapture.SanitizePrefix(string.IsNullOrEmpty(clip.Name) ? capture.TakePrefixSetting : clip.Name);
            EditorGUILayout.HelpBox(
                $"In play mode, writes {written} frames ({clip.Duration:0.###} s at {capture.FrameRate} fps" +
                (every > 1 ? $", every {every}th" : "") + $") as {prefix}_0000… into a take folder of their own. " +
                $"Starts {capture.WarmupFrames} warmup frames before the clip; between clips the timeline jumps ahead.",
                MessageType.None);
        }
    }
}
