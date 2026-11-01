using System;
using GaussianSplatting.Runtime;
using Tide.Editor;
using UnityEditor;

namespace GaussianSplatting.Editor
{
    internal sealed class GaussianParticleClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(GaussianParticleClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            if (context.Binding is not GaussianParticleEmitter emitter)
                return;
            EditorGUILayout.HelpBox($"Seeking restarts the particles {emitter.PrewarmTime:0.##} s before the new time " +
                "and simulates up to it; playing on from the start gives the same particles every take.", MessageType.None);
        }
    }
}
