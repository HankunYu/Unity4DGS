using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Custom IMGUI editor for GaussianMorph.
    /// Returns null from CreateInspectorGUI to force IMGUI path and avoid
    /// Unity 6 UIElements re-entrant visual-tree errors when
    /// [ExecuteInEditMode] LateUpdate modifies state during repaint.
    /// </summary>
    [CustomEditor(typeof(GaussianMorph))]
    [CanEditMultipleObjects]
    public class GaussianMorphEditor : UnityEditor.Editor
    {
        private float _lastWeight = -1f;

        // Disable UIElements inspector entirely — force IMGUI path
        public override VisualElement CreateInspectorGUI() => null;

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUI.BeginChangeCheck();
            DrawDefaultInspector();
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
            }

            // Repaint only when weight actually changes, instead of
            // RequiresConstantRepaint which triggers UIElements errors in Unity 6
            var morph = target as GaussianMorph;
            if (morph != null)
            {
                float w = morph.Weight;
                if (!Mathf.Approximately(w, _lastWeight))
                {
                    _lastWeight = w;
                    Repaint();
                }
            }
        }
    }
}
