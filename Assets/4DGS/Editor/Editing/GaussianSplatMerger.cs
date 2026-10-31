using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Multi-select merge logic, extracted from GaussianSplatRendererEditor.
    /// Draws merge UI when multiple Renderers are selected.
    /// </summary>
    internal static class GaussianSplatMerger
    {
        public static void DrawMultiEditGUI(Object[] targets, Object primaryTarget)
        {
            DrawSeparator();
            CountTargetSplats(targets, out int totalSplats, out int totalObjects);
            EditorGUILayout.LabelField("Total Objects", $"{totalObjects}");
            EditorGUILayout.LabelField("Total Splats", $"{totalSplats:N0}");
            if (totalSplats > GaussianSplatAsset.MaxSplats)
            {
                EditorGUILayout.HelpBox(
                    $"Can't merge, too many splats (max. supported {GaussianSplatAsset.MaxSplats:N0})",
                    MessageType.Warning);
                return;
            }

            var targetGs = primaryTarget as GaussianSplatRenderer;
            if (!targetGs || !targetGs.HasValidAsset || !targetGs.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox(
                    $"Can't merge into {primaryTarget.name} (no asset or disable)",
                    MessageType.Warning);
                return;
            }

            if (targetGs.asset.chunkData != null)
            {
                EditorGUILayout.HelpBox(
                    $"Can't merge into {primaryTarget.name} (needs to use Very High quality preset)",
                    MessageType.Warning);
                return;
            }

            if (GUILayout.Button($"Merge into {primaryTarget.name}"))
            {
                MergeSplatObjects(targets, targetGs);
            }
        }

        private static void CountTargetSplats(Object[] targets, out int totalSplats, out int totalObjects)
        {
            totalObjects = 0;
            totalSplats = 0;
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                ++totalObjects;
                totalSplats += gs.splatCount;
            }
        }

        private static void MergeSplatObjects(Object[] targets, GaussianSplatRenderer targetGs)
        {
            CountTargetSplats(targets, out int totalSplats, out _);
            if (totalSplats > GaussianSplatAsset.MaxSplats)
                return;

            int copyDstOffset = targetGs.splatCount;
            targetGs.EditSetSplatCount(totalSplats);
            foreach (var obj in targets)
            {
                var gs = obj as GaussianSplatRenderer;
                if (!gs || !gs.HasValidAsset || !gs.isActiveAndEnabled)
                    continue;
                if (gs == targetGs)
                    continue;
                gs.EditCopySplatsInto(targetGs, 0, copyDstOffset, gs.splatCount);
                copyDstOffset += gs.splatCount;
                gs.gameObject.SetActive(false);
            }

            Debug.Assert(copyDstOffset == totalSplats, $"Merge count mismatch, {copyDstOffset} vs {totalSplats}");
            Selection.activeObject = targetGs;
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(12f, true);
            GUILayout.Box(GUIContent.none, "sv_iconselector_sep", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUILayout.Space();
        }
    }
}
