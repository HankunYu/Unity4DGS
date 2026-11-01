using System;
using GaussianSplatting.Runtime;
using Tide.Editor;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    internal sealed class GaussianSequenceClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(GaussianSequenceClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            var clip = (GaussianSequenceClip)context.Clip;
            if (context.Binding is not GaussianPlayer player || player.sequence == null)
                return;
            GaussianSplatSequence sequence = player.sequence;
            EditorGUILayout.LabelField("Sequence", $"{sequence.FrameCount} frames at {sequence.frameRate:0.##} fps");
            if (GUILayout.Button("Fit To Sequence") && clip.Speed > 0f)
            {
                context.Edit("Fit Clip", () => clip.Duration =
                    Math.Max(1, sequence.FrameCount - clip.StartFrame) / (sequence.frameRate * clip.Speed));
            }
        }
    }
}
