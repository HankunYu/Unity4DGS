using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Custom IMGUI editor for GaussianAnimator.
    /// Forces IMGUI rendering to avoid UIElements visual-tree re-entrant
    /// update errors triggered by [ExecuteInEditMode] LateUpdate during
    /// the default UIElements Inspector repaint cycle.
    /// </summary>
    [CustomEditor(typeof(GaussianAnimator))]
    [CanEditMultipleObjects]
    public class GaussianAnimatorEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var animator = target as GaussianAnimator;
            if (!animator)
                return;

            // Show auto-collected volumes
            var volumes = animator.Volumes;
            EditorGUILayout.LabelField("Volumes", volumes.Count.ToString());
            if (volumes.Count > 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    foreach (var vol in volumes)
                        EditorGUILayout.ObjectField(vol, typeof(GaussianAnimVolume), true);
                }
            }

            if (targets.Length != 1)
                return;

            EditorGUILayout.Space();
            if (GUILayout.Button("Add Volume"))
            {
                var go = ObjectFactory.CreateGameObject("AnimVolume", typeof(GaussianAnimVolume));
                go.transform.SetParent(animator.transform, false);
                Undo.RegisterCreatedObjectUndo(go, "Add Anim Volume");
                Selection.activeGameObject = go;
            }
        }
    }
}
