using System.Collections.Generic;
using System.IO;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // The look preset buttons of the renderer inspector, and the questions
    // asked before a preset is saved or applied
    internal static class GaussianLookPresetGUI
    {
        private const string LastFolderKey = "GaussianSplatting.LookPresetFolder";

        // Emitters and links are layers of a look, not owners of one
        public static bool HasLook(GaussianSplatRenderer renderer) =>
            !renderer.TryGetComponent(out GaussianParticleEmitter _) &&
            !renderer.TryGetComponent(out GaussianParticleLinks _) &&
            !renderer.TryGetComponent(out GaussianGridLinks _);

        public static void Draw(GaussianSplatRenderer renderer)
        {
            EditorGUILayout.Space();
            GUILayout.Label("Look Preset", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Save...", "Save this renderer's style volumes, particle emitters and links, their styles and its look settings as a preset")))
                {
                    SaveWithDialog(renderer);
                    GUIUtility.ExitGUI();
                }
                if (EditorGUILayout.DropdownButton(new GUIContent("Apply", "Replace this renderer's look with a saved preset"), FocusType.Passive))
                    ShowApplyMenu(renderer);
            }
        }

        public static void SaveWithDialog(GaussianSplatRenderer renderer)
        {
            string folder = EditorPrefs.GetString(LastFolderKey, "Assets");
            if (!AssetDatabase.IsValidFolder(folder))
                folder = "Assets";
            string path = EditorUtility.SaveFilePanelInProject("Save Look Preset", renderer.name + "Look", "asset",
                "The layers go into a prefab of the same name next to it.", folder);
            if (string.IsNullOrEmpty(path))
                return;
            EditorPrefs.SetString(LastFolderKey, Path.GetDirectoryName(path));
            GaussianLookPreset preset = GaussianLookPresets.Save(renderer, path);
            EditorGUIUtility.PingObject(preset);
        }

        // Asks first: applying replaces layers and writes into style assets
        public static void ApplyWithDialog(GaussianLookPreset preset, GaussianSplatRenderer renderer)
        {
            List<Transform> layers = GaussianLookPresets.FindLayers(renderer);
            List<GaussianSplatStyle> styles = GaussianLookPresets.FindStylesToRestore(preset);
            string message = $"Apply '{preset.name}' to '{renderer.name}'?";
            if (layers.Count > 0)
                message += "\n\nReplaces: " + string.Join(", ", layers.Select(l => l.name));
            if (styles.Count > 0)
                message += "\n\nSets these styles back to their saved values: " + string.Join(", ", styles.Select(s => s.name));
            if (EditorUtility.DisplayDialog("Apply Look Preset", message, "Apply", "Cancel"))
                GaussianLookPresets.Apply(preset, renderer);
        }

        private static void ShowApplyMenu(GaussianSplatRenderer renderer)
        {
            var menu = new GenericMenu();
            string[] guids = AssetDatabase.FindAssets($"t:{nameof(GaussianLookPreset)}");
            foreach (string path in guids.Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p))
            {
                var preset = AssetDatabase.LoadAssetAtPath<GaussianLookPreset>(path);
                if (preset != null)
                    menu.AddItem(new GUIContent(preset.name), false, () => ApplyWithDialog(preset, renderer));
            }
            if (menu.GetItemCount() == 0)
                menu.AddDisabledItem(new GUIContent("No look presets in the project"));
            menu.ShowAsContext();
        }
    }
}
