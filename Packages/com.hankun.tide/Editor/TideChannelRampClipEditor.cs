using System;
using UnityEditor;
using UnityEngine;

namespace Tide.Editor
{
    internal sealed class TideChannelRampClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(TideChannelRampClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            var clip = (TideChannelRampClip)context.Clip;
            if (!context.TryChannel(out ITideChannelTarget target, out int index))
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("From ← Scene"))
                    context.ReadScene(() => context.Edit("Capture Value", () => clip.From = target.GetChannelValue(index)));
                if (GUILayout.Button("To ← Scene"))
                    context.ReadScene(() => context.Edit("Capture Value", () => clip.To = target.GetChannelValue(index)));
            }
        }

        public override void Initialise(TideClipEditorContext context)
        {
            if (context.TryChannel(out ITideChannelTarget target, out int index))
            {
                var clip = (TideChannelRampClip)context.Clip;
                clip.From = clip.To = target.GetChannelValue(index);
            }
        }
    }
}
