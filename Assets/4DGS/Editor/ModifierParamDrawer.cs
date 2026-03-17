using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomPropertyDrawer(typeof(ModifierParam))]
    public class ModifierParamDrawer : PropertyDrawer
    {
        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            if (!property.isExpanded)
                return EditorGUIUtility.singleLineHeight;

            var targetProp = property.FindPropertyRelative("target");
            var target = targetProp.objectReferenceValue as GaussianAnimModifier;
            string[] labels = target != null ? target.GetParamLabels() : null;
            bool hasTarget = labels != null;

            // header + target + enabled
            int lines = 3;

            if (hasTarget)
            {
                // Only count used slots across all params
                for (int p = 0; p < 4; p++)
                    lines += CountUsedSlots(labels, p);
            }
            else
            {
                // No target: show all 4 Vector4 fields (1 foldout header each, collapsed)
                lines += 4;
            }

            return lines * (EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing);
        }

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);

            var rect = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            float lineStep = EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;

            property.isExpanded = EditorGUI.Foldout(rect, property.isExpanded, label, true);
            rect.y += lineStep;

            if (!property.isExpanded)
            {
                EditorGUI.EndProperty();
                return;
            }

            EditorGUI.indentLevel++;

            // Target
            var targetProp = property.FindPropertyRelative("target");
            EditorGUI.PropertyField(rect, targetProp);
            rect.y += lineStep;

            // Enabled
            EditorGUI.PropertyField(rect, property.FindPropertyRelative("enabled"));
            rect.y += lineStep;

            // Get labels from target modifier
            var target = targetProp.objectReferenceValue as GaussianAnimModifier;
            string[] labels = target != null ? target.GetParamLabels() : null;

            string[] paramNames = { "param0", "param1", "param2", "param3" };

            if (labels == null)
            {
                // No target: show raw Vector4 foldouts
                for (int p = 0; p < 4; p++)
                {
                    var paramProp = property.FindPropertyRelative(paramNames[p]);
                    EditorGUI.PropertyField(rect, paramProp, new GUIContent($"Param {p}"), false);
                    rect.y += lineStep;
                }
            }
            else
            {
                // Has target: show only used slots with meaningful labels
                for (int p = 0; p < 4; p++)
                {
                    int baseIdx = p * 4;
                    if (CountUsedSlots(labels, p) == 0)
                        continue;

                    var paramProp = property.FindPropertyRelative(paramNames[p]);
                    for (int c = 0; c < 4; c++)
                    {
                        string slotLabel = labels[baseIdx + c];
                        if (string.IsNullOrEmpty(slotLabel))
                            continue;

                        string component = c switch { 0 => "x", 1 => "y", 2 => "z", _ => "w" };
                        var componentProp = paramProp.FindPropertyRelative(component);
                        EditorGUI.PropertyField(rect, componentProp, new GUIContent(slotLabel));
                        rect.y += lineStep;
                    }
                }
            }

            EditorGUI.indentLevel--;
            EditorGUI.EndProperty();
        }

        private static int CountUsedSlots(string[] labels, int paramIndex)
        {
            if (labels == null) return 0;
            int count = 0;
            int baseIdx = paramIndex * 4;
            for (int c = 0; c < 4; c++)
            {
                if (baseIdx + c < labels.Length && !string.IsNullOrEmpty(labels[baseIdx + c]))
                    count++;
            }
            return count;
        }
    }
}
