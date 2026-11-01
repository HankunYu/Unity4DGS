using System;
using UnityEngine;

namespace Tide.Editor
{
    internal sealed class TideChannelConstantClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(TideChannelConstantClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            var clip = (TideChannelConstantClip)context.Clip;
            if (!context.TryChannel(out ITideChannelTarget target, out int index))
                return;
            if (GUILayout.Button("Value ← Scene"))
                context.ReadScene(() => context.Edit("Capture Value", () => clip.Value = target.GetChannelValue(index)));
        }

        public override void Initialise(TideClipEditorContext context)
        {
            if (context.TryChannel(out ITideChannelTarget target, out int index))
                ((TideChannelConstantClip)context.Clip).Value = target.GetChannelValue(index);
        }
    }
}
