using GaussianSplatting.Runtime;
using UnityEditor;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianParticleEmitter))]
    [CanEditMultipleObjects]
    public class GaussianParticleEmitterEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var emitter = target as GaussianParticleEmitter;
            if (emitter != null && targets.Length == 1)
            {
                GaussianSplatRenderer source = emitter.Source;
                if (source == null)
                {
                    EditorGUILayout.HelpBox("Place this object under the GaussianSplatRenderer the particles should be born on.", MessageType.Warning);
                }
                else
                {
                    string clock = emitter.IsDriven ? "clock set by a timeline" : "clock runs by itself";
                    EditorGUILayout.HelpBox(
                        $"Born on {source.name}\n" +
                        $"About {emitter.BirthRate:N0} particles per second, {emitter.ShownCount:N0} shown at once; {clock}",
                        MessageType.None);
                }
            }
            DrawDefaultInspector();
        }
    }
}
