using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Custom IMGUI editor for GaussianMorph.
    /// Forces IMGUI rendering to avoid UIElements visual-tree re-entrant
    /// update errors triggered by [ExecuteInEditMode] LateUpdate during
    /// the default UIElements Inspector repaint cycle.
    /// </summary>
    [CustomEditor(typeof(GaussianMorph))]
    [CanEditMultipleObjects]
    public class GaussianMorphEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }
    }
}
