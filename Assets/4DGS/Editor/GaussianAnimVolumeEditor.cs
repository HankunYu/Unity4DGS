using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianAnimVolume))]
    [CanEditMultipleObjects]
    public class GaussianAnimVolumeEditor : UnityEditor.Editor
    {
        private static List<Type> _modifierTypes;
        private static List<string> _modifierNames;

        private static void EnsureModifierCache()
        {
            if (_modifierTypes != null)
                return;

            _modifierTypes = new List<Type>();
            _modifierNames = new List<string>();

            var types = TypeCache.GetTypesDerivedFrom<GaussianAnimModifier>();
            foreach (var type in types)
            {
                if (type.IsAbstract)
                    continue;
                _modifierTypes.Add(type);
                _modifierNames.Add(NicifyTypeName(type));
            }
        }

        // "TurbulenceModifier" -> "Turbulence", "WheatWaveModifier" -> "Wheat Wave"
        private static string NicifyTypeName(Type type)
        {
            string name = type.Name;
            if (name.EndsWith("Modifier"))
                name = name.Substring(0, name.Length - "Modifier".Length);
            // Insert space before each uppercase letter that follows a lowercase letter
            return Regex.Replace(name, "(?<=\\p{Ll})(?=\\p{Lu})", " ");
        }

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
            EnsureModifierCache();
            EditorGUILayout.Space();
            var rect = EditorGUILayout.GetControlRect();
            if (EditorGUI.DropdownButton(rect, new GUIContent("Add Modifier"), FocusType.Passive))
            {
                var menu = new GenericMenu();
                for (int i = 0; i < _modifierTypes.Count; i++)
                {
                    int idx = i;
                    bool alreadyExists = volume.GetComponent(_modifierTypes[idx]) != null;
                    if (alreadyExists)
                        menu.AddDisabledItem(new GUIContent(_modifierNames[idx]));
                    else
                        menu.AddItem(new GUIContent(_modifierNames[idx]), false, () =>
                            Undo.AddComponent(volume.gameObject, _modifierTypes[idx]));
                }
                menu.DropDown(rect);
            }
        }
    }
}
