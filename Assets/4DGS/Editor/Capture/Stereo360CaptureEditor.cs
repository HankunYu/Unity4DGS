using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(Stereo360Capture))]
    public class Stereo360CaptureEditor : SequenceCaptureEditor
    {
        private SerializedProperty m_PropProjection;
        private SerializedProperty m_PropEyeLayout;
        private SerializedProperty m_PropEyeSeparation;
        private SerializedProperty m_PropCubemapFaceSize;
        private SerializedProperty m_PropOutputWidth;

        private static readonly int[] WidthPresets = { 2048, 4096, 5760, 8192 };
        private static readonly string[] WidthPresetLabels = { "2K (2048)", "4K (4096)", "6K (5760)", "8K (8192)", "Custom" };

        protected override void OnEnable()
        {
            base.OnEnable();
            m_PropProjection = serializedObject.FindProperty("projection");
            m_PropEyeLayout = serializedObject.FindProperty("eyeLayout");
            m_PropEyeSeparation = serializedObject.FindProperty("eyeSeparation");
            m_PropCubemapFaceSize = serializedObject.FindProperty("cubemapFaceSize");
            m_PropOutputWidth = serializedObject.FindProperty("outputWidth");
        }

        protected override void DrawViewSettings()
        {
            EditorGUILayout.Space();
            GUILayout.Label("Projection", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropProjection);
            EditorGUILayout.PropertyField(m_PropEyeLayout);
            if (m_PropEyeLayout.intValue != (int)Stereo360EyeLayout.LeftEyeOnly)
            {
                EditorGUILayout.PropertyField(m_PropEyeSeparation);
            }
            if (m_PropProjection.intValue == (int)Stereo360Projection.Cubemap)
            {
                EditorGUILayout.PropertyField(m_PropCubemapFaceSize);
            }
        }

        protected override void DrawResolution()
        {
            int current = m_PropOutputWidth.intValue;
            int index = System.Array.IndexOf(WidthPresets, current);
            if (index < 0)
            {
                index = WidthPresets.Length;
            }

            int picked = EditorGUILayout.Popup("Output Width", index, WidthPresetLabels);
            if (picked != index && picked < WidthPresets.Length)
            {
                m_PropOutputWidth.intValue = WidthPresets[picked];
            }
            if (picked == WidthPresets.Length)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_PropOutputWidth, new GUIContent("Custom Width"));
                EditorGUI.indentLevel--;
            }
        }

        protected override void DrawViewEstimates(SequenceCapture capture)
        {
            Vector2Int eye = ((Stereo360Capture)capture).PerEyeResolution;
            EditorGUILayout.LabelField("Per Eye", $"{eye.x} x {eye.y}");
        }

        protected override void DrawViewValidation(SequenceCapture capture)
        {
            if (m_PropProjection.intValue == (int)Stereo360Projection.OmniDirectionalStereo)
            {
                var config = GaussianSplatRenderSystem.instance.Config;
                if (config == null)
                {
                    EditorGUILayout.HelpBox(
                        "ODS needs an active GaussianSplatConfig in the scene.", MessageType.Error);
                }
                else if (config.renderMode != GaussianSplatRenderMode.PointCloud)
                {
                    EditorGUILayout.HelpBox(
                        $"ODS requires renderMode = PointCloud (currently {config.renderMode}). The splat path " +
                        "never clamps the synthesised footprint, so a splat near the eye can hang the GPU.",
                        MessageType.Error);
                }
            }
            else
            {
                // Angular density match: an equirect of width W needs W/(2*pi) px
                // per radian, and a cube face of size N delivers N/2 px per radian
                // at its centre, so N = W/pi is the break-even point.
                float ideal = m_PropOutputWidth.intValue / Mathf.PI;
                int face = m_PropCubemapFaceSize.intValue;
                if (face < ideal * 0.9f)
                {
                    EditorGUILayout.HelpBox(
                        $"Cubemap face {face} undersamples the equirect (match is ~{Mathf.RoundToInt(ideal)}). " +
                        "The panorama will be soft.", MessageType.Warning);
                }
                else if (face > ideal * 1.5f)
                {
                    EditorGUILayout.HelpBox(
                        $"Cubemap face {face} is far above the match (~{Mathf.RoundToInt(ideal)}). " +
                        "The cube RT has no mip chain, so that minification aliases instead of filtering.",
                        MessageType.Warning);
                }

                EditorGUILayout.HelpBox(
                    "Cubemap projection applies a constant eye offset per face, so the eyes disagree along " +
                    "the 12 cube edges and at the 8 corners. Use ODS unless you need non-splat geometry.",
                    MessageType.Info);
            }
        }
    }
}
