using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianCutoutManager))]
    public class GaussianCutoutManagerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var mgr = target as GaussianCutoutManager;
            if (!mgr)
                return;

            var renderer = mgr.GetComponent<GaussianSplatRenderer>();

            // Show auto-collected cutouts
            EditorGUILayout.LabelField("Cutouts", mgr.CutoutCount.ToString());
            if (mgr.CutoutCount > 0)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    foreach (var cutout in mgr.Cutouts)
                        EditorGUILayout.ObjectField(cutout, typeof(GaussianCutout), true);
                }
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("Add Cutout"))
            {
                var go = ObjectFactory.CreateGameObject("GSCutout", typeof(GaussianCutout));
                var cutoutTr = go.transform;
                cutoutTr.SetParent(mgr.transform, false);

                if (renderer != null && renderer.HasValidAsset)
                {
                    var size = renderer.asset.boundsMax - renderer.asset.boundsMin;
                    float extent = Mathf.Max(size.x, Mathf.Max(size.y, size.z)) * 0.25f;
                    cutoutTr.localScale = Vector3.one * extent;
                }

                Undo.RegisterCreatedObjectUndo(go, "Add Cutout");
                Selection.activeGameObject = go;
            }
        }
    }
}
