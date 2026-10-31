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
            if (emitter != null)
            {
                GaussianSplatRenderer source = emitter.Source;
                if (source == null)
                    EditorGUILayout.HelpBox("Place this object under the GaussianSplatRenderer the particles should be born on.", MessageType.Warning);
                else
                    EditorGUILayout.HelpBox($"Born on {source.name}", MessageType.None);
            }
            DrawDefaultInspector();
        }
    }
}
