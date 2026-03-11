using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianSplatConfig))]
    public class GaussianSplatConfigEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropRenderMode;
        SerializedProperty m_PropPointDisplaySize;
        SerializedProperty m_PropUseTileRenderer;

        private void OnEnable()
        {
            m_PropRenderMode = serializedObject.FindProperty("renderMode");
            m_PropPointDisplaySize = serializedObject.FindProperty("pointDisplaySize");
            m_PropUseTileRenderer = serializedObject.FindProperty("useTileRenderer");
        }

        public override void OnInspectorGUI()
        {
            var config = target as GaussianSplatConfig;
            if (!config)
                return;

            serializedObject.Update();

            GUILayout.Label("Render Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropRenderMode);
            if (m_PropRenderMode.intValue is (int)GaussianSplatRenderMode.DebugPoints or (int)GaussianSplatRenderMode.DebugPointIndices)
                EditorGUILayout.PropertyField(m_PropPointDisplaySize);
            EditorGUILayout.PropertyField(m_PropUseTileRenderer, new GUIContent("Tile-Based Renderer"));

            EditorGUILayout.Space();
            GUILayout.Label("Auto-Loaded Resources", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Shader Splats", config.ShaderSplats, typeof(Shader), false);
                EditorGUILayout.ObjectField("Shader Composite", config.ShaderComposite, typeof(Shader), false);
                EditorGUILayout.ObjectField("Shader Debug Points", config.ShaderDebugPoints, typeof(Shader), false);
                EditorGUILayout.ObjectField("Shader Debug Boxes", config.ShaderDebugBoxes, typeof(Shader), false);
                EditorGUILayout.ObjectField("CS Splat Utilities", config.CsSplatUtilities, typeof(ComputeShader), false);
                EditorGUILayout.ObjectField("CS Tile Render", config.CsTileRender, typeof(ComputeShader), false);
            }

            if (!config.ResourcesValid)
            {
                EditorGUILayout.HelpBox(
                    "Some resources failed to load. Ensure all shaders are included in the build and SplatUtilities.compute is in a Resources folder.",
                    MessageType.Error);
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
