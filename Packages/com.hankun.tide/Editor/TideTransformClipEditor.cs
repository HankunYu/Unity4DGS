using System;
using UnityEditor;
using UnityEngine;

namespace Tide.Editor
{
    internal sealed class TideTransformClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(TideTransformClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            var clip = (TideTransformClip)context.Clip;
            if (context.Binding is not Transform transform)
                return;
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("From ← Scene"))
                {
                    context.ReadScene(() => context.Edit("Capture Pose", () =>
                    {
                        clip.FromPosition = transform.localPosition;
                        clip.FromRotation = transform.localEulerAngles;
                        clip.FromScale = transform.localScale;
                    }));
                }
                if (GUILayout.Button("To ← Scene"))
                {
                    context.ReadScene(() => context.Edit("Capture Pose", () =>
                    {
                        clip.ToPosition = transform.localPosition;
                        clip.ToRotation = transform.localEulerAngles;
                        clip.ToScale = transform.localScale;
                    }));
                }
            }
        }

        public override void Initialise(TideClipEditorContext context)
        {
            if (context.Binding is not Transform transform)
                return;
            var clip = (TideTransformClip)context.Clip;
            clip.FromPosition = clip.ToPosition = transform.localPosition;
            clip.FromRotation = clip.ToRotation = transform.localEulerAngles;
            clip.FromScale = clip.ToScale = transform.localScale;
        }
    }
}
