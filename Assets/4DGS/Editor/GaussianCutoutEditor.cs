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

            var mgr = cutout.GetComponentInParent<GaussianCutoutManager>();
            if (mgr == null)
            {
                EditorGUILayout.HelpBox(
                    "This cutout is not a child of a GaussianCutoutManager and will have no effect.",
                    MessageType.Warning);
            }
        }
    }
}
