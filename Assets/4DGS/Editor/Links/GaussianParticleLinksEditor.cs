using GaussianSplatting.Runtime;
using UnityEditor;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianParticleLinks))]
    [CanEditMultipleObjects]
    public class GaussianParticleLinksEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var links = target as GaussianParticleLinks;
            if (links != null && targets.Length == 1)
            {
                GaussianParticleEmitter emitter = links.Emitter;
                if (emitter == null)
                {
                    EditorGUILayout.HelpBox("Place this object right under the GaussianParticleEmitter whose particles should be linked.", MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox(
                        $"Links the particles of {emitter.name}\n" +
                        $"Up to {emitter.Capacity * links.LinksPerPoint:N0} links; draw them with a style of Segment streaks",
                        MessageType.None);
                }
            }
            DrawDefaultInspector();
        }
    }
}
