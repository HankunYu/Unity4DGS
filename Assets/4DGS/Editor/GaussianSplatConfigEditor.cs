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
        SerializedProperty m_PropPointCloudSizeScale;
        SerializedProperty m_PropPointCloudMinDisplaySize;
        SerializedProperty m_PropPointCloudMinWorldSize;
        SerializedProperty m_PropPointCloudMaxDisplaySize;
        SerializedProperty m_PropPointCloudOpacityBoost;
        SerializedProperty m_PropUseTileRenderer;
        SerializedProperty m_PropPointCloudAov;
        SerializedProperty m_PropPointCloudAovDepthScale;
        SerializedProperty m_PropProjectionMode;
        SerializedProperty m_PropOdsEyeOffset;
        SerializedProperty m_PropOdsPoleMergeStart;
        SerializedProperty m_PropOdsPoleMergeEnd;

        private void OnEnable()
        {
            m_PropProjectionMode = serializedObject.FindProperty("projectionMode");
            m_PropOdsEyeOffset = serializedObject.FindProperty("odsEyeOffset");
            m_PropOdsPoleMergeStart = serializedObject.FindProperty("odsPoleMergeStart");
            m_PropOdsPoleMergeEnd = serializedObject.FindProperty("odsPoleMergeEnd");
            m_PropRenderMode = serializedObject.FindProperty("renderMode");
            m_PropPointDisplaySize = serializedObject.FindProperty("pointDisplaySize");
            m_PropPointCloudSizeScale = serializedObject.FindProperty("pointCloudSizeScale");
            m_PropPointCloudMinDisplaySize = serializedObject.FindProperty("pointCloudMinDisplaySize");
            m_PropPointCloudMinWorldSize = serializedObject.FindProperty("pointCloudMinWorldSize");
            m_PropPointCloudMaxDisplaySize = serializedObject.FindProperty("pointCloudMaxDisplaySize");
            m_PropPointCloudOpacityBoost = serializedObject.FindProperty("pointCloudOpacityBoost");
            m_PropUseTileRenderer = serializedObject.FindProperty("useTileRenderer");
            m_PropPointCloudAov = serializedObject.FindProperty("pointCloudAov");
            m_PropPointCloudAovDepthScale = serializedObject.FindProperty("pointCloudAovDepthScale");
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
            if (m_PropRenderMode.intValue is (int)GaussianSplatRenderMode.PointCloud)
            {
                EditorGUILayout.PropertyField(m_PropPointCloudSizeScale);
                EditorGUILayout.PropertyField(m_PropPointCloudMinDisplaySize);
                EditorGUILayout.PropertyField(m_PropPointCloudMinWorldSize);
                EditorGUILayout.PropertyField(m_PropPointCloudMaxDisplaySize);
                EditorGUILayout.PropertyField(m_PropPointCloudOpacityBoost);
                EditorGUILayout.PropertyField(m_PropPointCloudAov, new GUIContent("AOV Channel"));
                if (m_PropPointCloudAov.intValue != (int)GaussianSplatAovMode.None)
                {
                    EditorGUILayout.PropertyField(m_PropPointCloudAovDepthScale, new GUIContent("Depth Scale"));
                    float sc = m_PropPointCloudAovDepthScale.floatValue;
                    if (sc > 0f)
                    {
                        EditorGUILayout.LabelField(" ",
                            $"stored = metres x {sc:0.###}   |   1.0 in the file = {1f / sc:0.##} m",
                            EditorStyles.miniLabel);
                    }
                    EditorGUILayout.HelpBox(
                        "An AOV replaces the beauty image for the whole run — it is raw data, not a picture. " +
                        "Capture it with EXR output; 8-bit PNG cannot carry it. Depth is linear metres, " +
                        "background 0, alpha is the coverage matte. Under ODS it is radial distance, not " +
                        "planar depth. AOV mattes are hard-edged by construction: coverage can be blended, " +
                        "a depth value cannot.", MessageType.Info);
                }
            }
            EditorGUILayout.PropertyField(m_PropUseTileRenderer, new GUIContent("Tile-Based Renderer"));

            EditorGUILayout.Space();
            GUILayout.Label("Projection", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropProjectionMode);
            if (m_PropProjectionMode.intValue == (int)GaussianSplatProjectionMode.OmniDirectionalStereo)
            {
                EditorGUILayout.PropertyField(m_PropOdsEyeOffset, new GUIContent("Eye Offset (m)"));
                EditorGUILayout.PropertyField(m_PropOdsPoleMergeStart, new GUIContent("Pole Merge Start (deg)"));
                EditorGUILayout.PropertyField(m_PropOdsPoleMergeEnd, new GUIContent("Pole Merge End (deg)"));
                EditorGUILayout.HelpBox(
                    "ODS projects the whole sphere into one 2:1 equirect image and only applies to splats — " +
                    "other geometry on the same camera still uses a normal frustum. Stereo360Capture drives " +
                    "the mode and eye offset per frame; setting them here is for previewing a single eye.",
                    MessageType.Info);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Auto-Loaded Resources", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Shader Splats", config.ShaderSplats, typeof(Shader), false);
                EditorGUILayout.ObjectField("Shader Composite", config.ShaderComposite, typeof(Shader), false);
                EditorGUILayout.ObjectField("Shader Debug Points", config.ShaderDebugPoints, typeof(Shader), false);
                EditorGUILayout.ObjectField("Shader Debug Boxes", config.ShaderDebugBoxes, typeof(Shader), false);
                EditorGUILayout.ObjectField("Shader Point Cloud", config.ShaderPointCloud, typeof(Shader), false);
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
