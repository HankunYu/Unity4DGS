using System;
using UnityEditor;
using UnityEngine;

namespace Tide.Editor
{
    internal sealed class TideSubTimelineClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(TideSubTimelineClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            var clip = (TideSubTimelineClip)context.Clip;
            if (context.Binding is not TidePlayer child)
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Fit To Sub-Timeline") && clip.TimeScale > 0f)
                {
                    context.Edit("Fit Clip", () => clip.Duration =
                        Math.Max(TideClip.MinDuration, (child.Duration - clip.ClipIn) / clip.TimeScale));
                }
                if (GUILayout.Button("Open"))
                    context.OpenPlayer(child);
            }
        }

        public override void Initialise(TideClipEditorContext context)
        {
            if (context.Binding is TidePlayer child && child.Duration > 0.0)
                context.Clip.Duration = child.Duration;
        }
    }
}
