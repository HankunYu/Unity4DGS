using System;
using UnityEditor;
using UnityEngine;

namespace Tide.Editor
{
    internal sealed class TideAnimationClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(TideAnimationClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            var clip = (TideAnimationClip)context.Clip;
            AnimationClip animation = clip.Animation;
            if (animation == null)
                return;
            EditorGUILayout.LabelField("Animation", $"{animation.length:0.##} s at {animation.frameRate:0.##} fps");
            if (GUILayout.Button("Fit To Animation") && clip.Speed > 0f)
            {
                context.Edit("Fit Clip", () => clip.Duration =
                    Math.Max(TideClip.MinDuration, (animation.length - clip.ClipIn) / clip.Speed));
            }
        }
    }
}
