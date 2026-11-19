using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianLookPreset))]
    public class GaussianLookPresetEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var preset = (GaussianLookPreset)target;
            // Presets are written by GaussianLookPresets.Save only
            bool wasEnabled = GUI.enabled;
            GUI.enabled = true;

            if (preset.Layers == null)
            {
                EditorGUILayout.HelpBox("The layers prefab is missing. Save the look again.", MessageType.Error);
            }
            else
            {
                string layers = string.Join(", ", preset.Layers.transform.Cast<Transform>().Select(t => t.name));
                EditorGUILayout.LabelField("Layers", layers.Length > 0 ? layers : "None", EditorStyles.wordWrappedLabel);
            }
            string styles = string.Join(", ", preset.Styles.Where(s => s.Snapshot != null).Select(s => s.Snapshot.name));
            EditorGUILayout.LabelField("Styles", styles.Length > 0 ? styles : "None", EditorStyles.wordWrappedLabel);
            string components = string.Join(", ", preset.Components.Where(c => c != null).Select(c => c.name));
            EditorGUILayout.LabelField("Settings", components, EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space();
            GameObject selected = Selection.activeGameObject;
            GaussianSplatRenderer renderer = selected != null ? selected.GetComponent<GaussianSplatRenderer>() : null;
            bool canApply = renderer != null && GaussianLookPresetGUI.HasLook(renderer) && preset.Layers != null &&
                !EditorApplication.isPlayingOrWillChangePlaymode;
            using (new EditorGUI.DisabledScope(!canApply))
            {
                string label = renderer != null ? $"Apply to '{renderer.name}'" : "Apply to Selected Renderer";
                if (GUILayout.Button(label))
                    GaussianLookPresetGUI.ApplyWithDialog(preset, renderer);
            }
            if (renderer == null)
                EditorGUILayout.HelpBox("Lock this inspector and select a renderer, or use Apply in the renderer's own inspector.", MessageType.Info);

            GUI.enabled = wasEnabled;
        }
    }
}
