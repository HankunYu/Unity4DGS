using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianCutout))]
    public class GaussianCutoutEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var cutout = target as GaussianCutout;
            if (!cutout)
                return;

            var owner = GaussianHierarchy.OwningRenderer(cutout);
            if (owner == null || owner.GetComponent<GaussianCutoutManager>() == null)
            {
                EditorGUILayout.HelpBox(
                    "The renderer this cutout belongs to (the nearest one above it) has no GaussianCutoutManager, so the cutout has no effect.",
                    MessageType.Warning);
            }
        }
    }
}
