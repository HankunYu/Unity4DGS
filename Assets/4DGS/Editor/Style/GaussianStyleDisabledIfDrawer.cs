// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomPropertyDrawer(typeof(GaussianStyleDisabledIfAttribute))]
    internal class GaussianStyleDisabledIfDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            string field = ((GaussianStyleDisabledIfAttribute)attribute).Field;
            string path = property.propertyPath;
            int dot = path.LastIndexOf('.');
            SerializedProperty condition = property.serializedObject.FindProperty(dot < 0 ? field : path.Substring(0, dot + 1) + field);
            using (new EditorGUI.DisabledScope(condition != null && condition.boolValue))
                EditorGUI.PropertyField(position, property, label, true);
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) =>
            EditorGUI.GetPropertyHeight(property, label, true);
    }
}
