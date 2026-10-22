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
        SerializedProperty m_PropCompositeMode;
        SerializedProperty m_PropSplatQuadSigma;
        SerializedProperty m_PropAdaptiveSplatExtent;
        SerializedProperty m_PropMaxSplatPixelRadius;
        SerializedProperty m_PropMaxSHOrder;
        SerializedProperty m_PropSortMetric;
        SerializedProperty m_PropSortMoveThreshold;

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
            m_PropCompositeMode = serializedObject.FindProperty("compositeMode");
            m_PropSplatQuadSigma = serializedObject.FindProperty("splatQuadSigma");
            m_PropAdaptiveSplatExtent = serializedObject.FindProperty("adaptiveSplatExtent");
            m_PropMaxSplatPixelRadius = serializedObject.FindProperty("maxSplatPixelRadius");
            m_PropMaxSHOrder = serializedObject.FindProperty("maxSHOrder");
            m_PropSortMetric = serializedObject.FindProperty("sortMetric");
            m_PropSortMoveThreshold = serializedObject.FindProperty("sortMoveThreshold");
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
            GUILayout.Label("Performance", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropCompositeMode);
            if (m_PropCompositeMode.intValue == (int)GaussianSplatCompositeMode.Direct)
            {
                EditorGUILayout.HelpBox(
                    "Direct draws splats straight into the camera target: no intermediate RT, no composite " +
                    "pass, scene depth occludes splats, and XR foveation applies to them. Blending happens " +
                    "in linear space, so soft edges look slightly different from Offscreen. The tile " +
                    "renderer, Gaussian-only stylize, AOVs, ODS and the debug modes stay offscreen.",
                    MessageType.Info);
            }
            EditorGUILayout.PropertyField(m_PropSplatQuadSigma, new GUIContent("Quad Extent (sigma)"));
            EditorGUILayout.PropertyField(m_PropAdaptiveSplatExtent, new GUIContent("Opacity-Adaptive Quads"));
            EditorGUILayout.PropertyField(m_PropMaxSplatPixelRadius, new GUIContent("Max Splat Radius (px)"));
            EditorGUILayout.PropertyField(m_PropMaxSHOrder, new GUIContent("Max SH Order"));

            EditorGUILayout.Space();
            GUILayout.Label("Sorting", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropSortMetric);
            using (new EditorGUI.DisabledScope(m_PropSortMetric.intValue != (int)GaussianSplatSortMetric.Radial))
                EditorGUILayout.PropertyField(m_PropSortMoveThreshold, new GUIContent("Re-sort Distance (m)"));

            EditorGUILayout.Space();
            if (GUILayout.Button(new GUIContent("Apply VR Preset",
                    "Direct composite, sqrt(5) sigma quads, 512 px radius cap, SH order 1, radial sorting " +
                    "re-sorted every 1 cm of head motion, tile renderer off. A starting point for " +
                    "standalone headsets; tune from there.")))
            {
                ApplyVrPreset();
            }

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

        // Values follow Spark's VR guidance (sqrt(5) sigma, 512 px radius cap)
        // plus this renderer's own bandwidth levers. Edits the serialized
        // properties so the change is undoable and marks the scene dirty.
        private void ApplyVrPreset()
        {
            m_PropCompositeMode.intValue = (int)GaussianSplatCompositeMode.Direct;
            m_PropSplatQuadSigma.floatValue = Mathf.Sqrt(5f);
            m_PropAdaptiveSplatExtent.boolValue = true;
            m_PropMaxSplatPixelRadius.floatValue = 512f;
            m_PropMaxSHOrder.intValue = 1;
            m_PropSortMetric.intValue = (int)GaussianSplatSortMetric.Radial;
            m_PropSortMoveThreshold.floatValue = 0.01f;
            m_PropUseTileRenderer.boolValue = false;
        }
    }
}
