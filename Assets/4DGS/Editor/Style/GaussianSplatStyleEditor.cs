// SPDX-License-Identifier: MIT

using System;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // A style as the list of its blocks: each with its settings, a switch
    // and a way to remove it, and a menu of the blocks it does not have yet
    [CustomEditor(typeof(GaussianSplatStyle))]
    internal class GaussianSplatStyleEditor : UnityEditor.Editor
    {
        private static Type[] _blockTypes;

        // Every kind of block there is, by name
        private static Type[] BlockTypes => _blockTypes ??= TypeCache.GetTypesDerivedFrom<GaussianStyleBlock>()
            .Where(type => !type.IsAbstract && !type.IsGenericType)
            .OrderBy(type => BlockName(type))
            .ToArray();

        public override void OnInspectorGUI()
        {
            var style = (GaussianSplatStyle)target;
            serializedObject.Update();
            SerializedProperty blocks = serializedObject.FindProperty("blocks");
            Type removed = null;
            for (int i = 0; i < blocks.arraySize; i++)
            {
                SerializedProperty block = blocks.GetArrayElementAtIndex(i);
                if (block.managedReferenceValue == null)
                    continue;
                if (DrawBlock(block))
                    removed = block.managedReferenceValue.GetType();
            }
            serializedObject.ApplyModifiedProperties();

            DrawWarnings(style);
            EditorGUILayout.Space();
            DrawAddButton(style);

            if (removed != null)
            {
                Undo.RecordObject(style, "Remove Style Block");
                style.Remove(removed);
                EditorUtility.SetDirty(style);
            }
        }

        // True when the block's remove button was pressed
        private static bool DrawBlock(SerializedProperty block)
        {
            SerializedProperty enabled = block.FindPropertyRelative("enabled");
            string name = BlockName(block.managedReferenceValue.GetType());

            Rect header = EditorGUILayout.GetControlRect();
            EditorGUI.DrawRect(new Rect(header.x - 16f, header.y - 1f, header.width + 20f, 1f), new Color(0f, 0f, 0f, 0.3f));
            var toggle = new Rect(header.x + 14f, header.y, 18f, header.height);
            var remove = new Rect(header.xMax - 22f, header.y, 22f, header.height);
            var label = new Rect(toggle.xMax, header.y, remove.x - toggle.xMax, header.height);

            block.isExpanded = EditorGUI.Foldout(new Rect(header.x, header.y, 14f, header.height), block.isExpanded, GUIContent.none);
            enabled.boolValue = EditorGUI.Toggle(toggle, enabled.boolValue);
            EditorGUI.LabelField(label, name, EditorStyles.boldLabel);
            if (Event.current.type == EventType.MouseDown && label.Contains(Event.current.mousePosition))
            {
                block.isExpanded = !block.isExpanded;
                Event.current.Use();
            }
            bool removed = GUI.Button(remove, new GUIContent("×", "Remove this block"), EditorStyles.miniButton);

            if (block.isExpanded)
            {
                using (new EditorGUI.DisabledScope(!enabled.boolValue))
                using (new EditorGUI.IndentLevelScope())
                {
                    SerializedProperty child = block.Copy();
                    SerializedProperty end = block.GetEndProperty();
                    for (bool more = child.NextVisible(true); more && !SerializedProperty.EqualContents(child, end); more = child.NextVisible(false))
                        EditorGUILayout.PropertyField(child, true);
                }
                EditorGUILayout.Space(2f);
            }
            return removed;
        }

        // Blocks that read another block the style lacks
        private static void DrawWarnings(GaussianSplatStyle style)
        {
            if (style.TryGet(out GaussianStyleColorBlock color) &&
                color.Source == GaussianSplatStyleColorSource.Ramp &&
                color.Input == GaussianSplatStyleRampInput.VolumeDepth &&
                !style.TryGet(out GaussianStyleDepthBlock _))
            {
                EditorGUILayout.HelpBox("The Volume Depth ramp input reads the Depth block's mask. Add a Depth block; its amount can stay at 0.", MessageType.Warning);
            }
            if (style.TryGet(out GaussianStyleFlowBlock flow) && flow.AlignToGrid && !style.TryGet(out GaussianStyleQuantizeBlock _))
                EditorGUILayout.HelpBox("Flow is set to align to the grid, but the style has no Quantize block: its own column size and step apply.", MessageType.Info);
        }

        private static void DrawAddButton(GaussianSplatStyle style)
        {
            Type[] missing = BlockTypes.Where(type => style.Find(type) == null).ToArray();
            using (new EditorGUI.DisabledScope(missing.Length == 0))
            {
                Rect rect = EditorGUILayout.GetControlRect();
                if (!EditorGUI.DropdownButton(rect, new GUIContent("Add Block"), FocusType.Keyboard))
                    return;
                var menu = new GenericMenu();
                foreach (Type type in missing)
                {
                    menu.AddItem(new GUIContent(BlockName(type)), false, () =>
                    {
                        Undo.RecordObject(style, "Add Style Block");
                        style.Add(type);
                        EditorUtility.SetDirty(style);
                    });
                }
                menu.DropDown(rect);
            }
        }

        // GaussianStyleRimBlock -> Rim
        private static string BlockName(Type type)
        {
            string name = type.Name;
            if (name.StartsWith("GaussianStyle"))
                name = name.Substring("GaussianStyle".Length);
            if (name.EndsWith("Block"))
                name = name.Substring(0, name.Length - "Block".Length);
            return ObjectNames.NicifyVariableName(name);
        }
    }
}
