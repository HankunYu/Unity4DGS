using System;
using System.Linq;
using GaussianSplatting.Runtime;
using Tide.Editor;
using UnityEditor;

namespace GaussianSplatting.Editor
{
    internal sealed class GaussianModifierStateClipEditor : TideClipEditor
    {
        public override Type ClipType => typeof(GaussianModifierStateClip);

        public override void OnToolsGUI(TideClipEditorContext context)
        {
            var clip = (GaussianModifierStateClip)context.Clip;
            if (context.Binding is not GaussianModifierStateMachine machine)
                return;
            string[] states = machine.States.Select(s => s.name).ToArray();
            if (states.Length == 0)
            {
                EditorGUILayout.HelpBox($"{machine.name} has no states.", MessageType.Info);
                return;
            }
            EditorGUI.BeginChangeCheck();
            int picked = EditorGUILayout.Popup("Pick State", Math.Max(0, Array.IndexOf(states, clip.State)), states);
            if (EditorGUI.EndChangeCheck())
                context.Edit("Pick State", () => clip.State = states[picked]);
        }

        public override void Initialise(TideClipEditorContext context)
        {
            if (context.Binding is GaussianModifierStateMachine machine && machine.States.Count > 0)
                ((GaussianModifierStateClip)context.Clip).State = machine.States[0].name;
        }
    }
}
