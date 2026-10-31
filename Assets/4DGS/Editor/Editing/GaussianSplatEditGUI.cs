using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.EditorTools;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Draws the edit tools section (Edit/Reset, cutout info, stats) in the
    /// Renderer Inspector. Extracted from GaussianSplatRendererEditor.
    /// </summary>
    internal static class GaussianSplatEditGUI
    {
        private static int s_editStatsUpdateCounter;

        public static void BumpGUICounter()
        {
            ++s_editStatsUpdateCounter;
        }

        public static void Draw(GaussianSplatRenderer gs)
        {
            ++s_editStatsUpdateCounter;

            DrawSeparator();

            // Edit mode toggle + Reset
            bool wasToolActive = ToolManager.activeContextType == typeof(GaussianToolContext);
            GUILayout.BeginHorizontal();
            bool isToolActive = GUILayout.Toggle(wasToolActive, "Edit", EditorStyles.miniButton);
            using (new EditorGUI.DisabledScope(!gs.editModified))
            {
                if (GUILayout.Button("Reset", GUILayout.ExpandWidth(false)))
                {
                    if (EditorUtility.DisplayDialog("Reset Splat Modifications?",
                            $"This will reset edits of {gs.name} to match the {gs.asset.name} asset. Continue?",
                            "Yes, reset", "Cancel"))
                    {
                        gs.enabled = false;
                        gs.enabled = true;
                    }
                }
            }
            GUILayout.EndHorizontal();

            if (!wasToolActive && isToolActive)
            {
                ToolManager.SetActiveContext<GaussianToolContext>();
                if (Tools.current == Tool.View)
                    Tools.current = Tool.Move;
            }

            if (wasToolActive && !isToolActive)
            {
                ToolManager.SetActiveContext<GameObjectToolContext>();
            }

            if (isToolActive && gs.asset.chunkData != null)
            {
                EditorGUILayout.HelpBox("Splat move/rotate/scale tools need Very High splat quality preset", MessageType.Warning);
            }

            // Cutout info
            EditorGUILayout.Space();
            GUILayout.Label("Cutouts", EditorStyles.boldLabel);
            var cutoutMgr = gs.CutoutManager;
            if (cutoutMgr == null)
            {
                if (GUILayout.Button("Add Cutout Manager"))
                {
                    Undo.AddComponent<GaussianCutoutManager>(gs.gameObject);
                }
            }
            else
            {
                EditorGUILayout.LabelField("Registered Cutouts", cutoutMgr.CutoutCount.ToString());
            }

            bool hasCutouts = cutoutMgr != null && cutoutMgr.CutoutCount > 0;

            // Stats
            bool displayEditStats = isToolActive || gs.editModified || hasCutouts;
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Splats", $"{gs.splatCount:N0}");

            var system = GaussianSplatRenderSystem.instance;
            if (system != null && system.Config != null && system.Config.useTileRenderer)
            {
                uint pairCount = system.LastTilePairCount;
                if (pairCount > 0)
                {
                    int max = GaussianSplatRenderSystem.MaxTilePairsCapacity;
                    float usage = (float)pairCount / max * 100f;
                    EditorGUILayout.LabelField("Tile Pairs", $"{pairCount:N0} / {max:N0} ({usage:F1}%)");
                }
            }

            if (displayEditStats)
            {
                EditorGUILayout.LabelField("Cut", $"{gs.editCutSplats:N0}");
                EditorGUILayout.LabelField("Deleted", $"{gs.editDeletedSplats:N0}");
                EditorGUILayout.LabelField("Selected", $"{gs.editSelectedSplats:N0}");
                if (hasCutouts && s_editStatsUpdateCounter > 10)
                {
                    gs.UpdateEditCountsAndBounds();
                    s_editStatsUpdateCounter = 0;
                }
            }
        }

        private static void DrawSeparator()
        {
            EditorGUILayout.Space(12f, true);
            GUILayout.Box(GUIContent.none, "sv_iconselector_sep", GUILayout.Height(2), GUILayout.ExpandWidth(true));
            EditorGUILayout.Space();
        }
    }
}
