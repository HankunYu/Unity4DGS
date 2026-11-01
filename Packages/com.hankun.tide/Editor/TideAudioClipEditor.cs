using System;
using UnityEngine;

namespace Tide.Editor
{
    internal sealed class TideAudioClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(TideAudioClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            var clip = (TideAudioClip)context.Clip;
            if (clip.Audio != null && GUILayout.Button("Fit To Audio"))
                context.Edit("Fit Clip", () => clip.Duration = clip.Audio.length);
        }

        public override void Initialise(TideClipEditorContext context)
        {
            if (context.Binding is AudioSource source && source.clip != null)
            {
                var clip = (TideAudioClip)context.Clip;
                clip.Audio = source.clip;
                clip.Duration = source.clip.length;
            }
        }
    }
}
