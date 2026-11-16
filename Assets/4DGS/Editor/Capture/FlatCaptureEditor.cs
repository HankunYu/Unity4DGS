using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(FlatCapture))]
    public class FlatCaptureEditor : SequenceCaptureEditor
    {
        private SerializedProperty m_PropOutputWidth;
        private SerializedProperty m_PropOutputHeight;

        private static readonly Vector2Int[] SizePresets =
        {
            new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(2560, 1440), new Vector2Int(3840, 2160)
        };
        private static readonly string[] SizePresetLabels =
        {
            "720p (1280 x 720)", "1080p (1920 x 1080)", "1440p (2560 x 1440)", "4K (3840 x 2160)", "Custom"
        };

        protected override void OnEnable()
        {
            base.OnEnable();
            m_PropOutputWidth = serializedObject.FindProperty("outputWidth");
            m_PropOutputHeight = serializedObject.FindProperty("outputHeight");
        }

        protected override void DrawResolution()
        {
            var current = new Vector2Int(m_PropOutputWidth.intValue, m_PropOutputHeight.intValue);
            int index = System.Array.IndexOf(SizePresets, current);
            if (index < 0)
            {
                index = SizePresets.Length;
            }

            int picked = EditorGUILayout.Popup("Resolution", index, SizePresetLabels);
            if (picked != index && picked < SizePresets.Length)
            {
                m_PropOutputWidth.intValue = SizePresets[picked].x;
                m_PropOutputHeight.intValue = SizePresets[picked].y;
            }
            if (picked == SizePresets.Length)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_PropOutputWidth, new GUIContent("Width"));
                EditorGUILayout.PropertyField(m_PropOutputHeight, new GUIContent("Height"));
                EditorGUI.indentLevel--;
            }
        }

        // The splat system's projection is a scene-wide setting a 360 capture
        // switches while it renders; a flat frame wants the ordinary one
        protected override void DrawViewValidation(SequenceCapture capture)
        {
            var config = GaussianSplatRenderSystem.instance.Config;
            if (config != null && config.projectionMode != GaussianSplatProjectionMode.Perspective)
            {
                EditorGUILayout.HelpBox(
                    $"GaussianSplatConfig.projectionMode is {config.projectionMode}: the frames will show that " +
                    "projection, not the camera's perspective view.", MessageType.Warning);
            }
        }
    }
}
