using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianAnimVolume))]
    [CanEditMultipleObjects]
    public class GaussianAnimVolumeEditor : UnityEditor.Editor
    {
        private static readonly string[] ModifierNames = { "Dissolve", "Wave", "Warp", "Property" };
        private static readonly System.Type[] ModifierTypes =
        {
            typeof(DissolveModifier),
            typeof(WaveModifier),
            typeof(WarpModifier),
            typeof(PropertyModifier)
        };

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (targets.Length != 1)
                return;

            var volume = target as GaussianAnimVolume;
            if (!volume)
                return;

            // Show existing modifiers
            var mods = volume.GetModifiers();
            if (mods.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Modifiers", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(true))
                {
                    foreach (var mod in mods)
                        EditorGUILayout.ObjectField(mod, typeof(GaussianAnimModifier), true);
                }
            }

            // Add modifier dropdown
            EditorGUILayout.Space();
            var rect = EditorGUILayout.GetControlRect();
            if (EditorGUI.DropdownButton(rect, new GUIContent("Add Modifier"), FocusType.Passive))
            {
                var menu = new GenericMenu();
                for (int i = 0; i < ModifierNames.Length; i++)
                {
                    int idx = i;
                    bool alreadyExists = volume.GetComponent(ModifierTypes[idx]) != null;
                    if (alreadyExists)
                        menu.AddDisabledItem(new GUIContent(ModifierNames[idx]));
                    else
                        menu.AddItem(new GUIContent(ModifierNames[idx]), false, () => AddModifier(volume, idx));
                }
                menu.DropDown(rect);
            }
        }

        private static void AddModifier(GaussianAnimVolume volume, int typeIndex)
        {
            Undo.AddComponent(volume.gameObject, ModifierTypes[typeIndex]);
        }
    }
}
