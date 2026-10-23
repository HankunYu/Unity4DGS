using System;
using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace GaussianSplatting.Editor.Import
{
    [CustomEditor(typeof(GaussianSplatImporter))]
    [CanEditMultipleObjects]
    public class GaussianSplatImporterEditor : ScriptedImporterEditor
    {
        static readonly GUIContent s_PruneLabel = new("Prune Opacity Below",
            "Drop splats less opaque than this at import. They still cost sorting and per-frame " +
            "view-data work. The renderer culls below ~0.02 at opacity scale 1, so values up to " +
            "that change nothing on screen unless a renderer raises its opacity scale. 0 keeps all.");
        static readonly GUIContent s_CamerasLabel = new("Import Cameras",
            "Read the training cameras from a cameras.json next to the source file.");
        static readonly GUIContent s_BudgetLabel = new("Splat Budget",
            "Keep only the most important splats (opacity times projected size), by count or by " +
            "percentage of the source. Sorting and view-data cost grow with the splat count every " +
            "frame. Applied after pruning by opacity.");
        static readonly GUIContent s_BudgetCountLabel = new("Max Splats");
        static readonly GUIContent s_BudgetPercentLabel = new("Keep Percent");
        static readonly GUIContent s_MaxSHDegreeLabel = new("Max SH Degree",
            "Drop SH bands above this at import. Each band adds view-dependent color, and data the " +
            "renderer reads for every splat each frame: degree 3 stores 15 coefficients per splat, " +
            "1 stores 3, 0 none (base color only).");

        SerializedProperty _quality;
        SerializedProperty _customFormats;
        SerializedProperty _pruneOpacity;
        SerializedProperty _importCameras;
        SerializedProperty _maxSHDegree;
        SerializedProperty _budgetMode;
        SerializedProperty _budgetCount;
        SerializedProperty _budgetPercent;

        string _headerPath;
        SplatSourceHeader _header;
        long _fileSize;
        string _headerError;

        public override void OnEnable()
        {
            base.OnEnable();
            var settings = serializedObject.FindProperty("settings");
            _quality = settings.FindPropertyRelative(nameof(GaussianSplatImportSettings.quality));
            _customFormats = settings.FindPropertyRelative(nameof(GaussianSplatImportSettings.customFormats));
            _pruneOpacity = settings.FindPropertyRelative(nameof(GaussianSplatImportSettings.pruneOpacity));
            _importCameras = settings.FindPropertyRelative(nameof(GaussianSplatImportSettings.importCameras));
            _maxSHDegree = settings.FindPropertyRelative(nameof(GaussianSplatImportSettings.maxSHDegree));
            _budgetMode = settings.FindPropertyRelative(nameof(GaussianSplatImportSettings.budgetMode));
            _budgetCount = settings.FindPropertyRelative(nameof(GaussianSplatImportSettings.budgetCount));
            _budgetPercent = settings.FindPropertyRelative(nameof(GaussianSplatImportSettings.budgetPercent));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (targets.Length == 1)
                DrawSourceInfo(((AssetImporter)target).assetPath);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Import Settings", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_importCameras, s_CamerasLabel);
            EditorGUILayout.PropertyField(_pruneOpacity, s_PruneLabel);
            DrawBudget();
            EditorGUILayout.PropertyField(_maxSHDegree, s_MaxSHDegreeLabel);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(_quality);
            bool enteredCustom = EditorGUI.EndChangeCheck() && !_quality.hasMultipleDifferentValues &&
                                 (GaussianSplatQuality)_quality.enumValueIndex == GaussianSplatQuality.Custom;
            if (!_quality.hasMultipleDifferentValues)
            {
                var quality = (GaussianSplatQuality)_quality.enumValueIndex;
                if (enteredCustom)
                    CopyPresetToCustom();
                if (quality == GaussianSplatQuality.Custom)
                {
                    EditorGUI.indentLevel++;
                    DrawCustomFormats();
                    EditorGUI.indentLevel--;
                }
                else
                {
                    DrawPresetFormats(GaussianSplatFormats.ForQuality(quality));
                }
            }

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }

        void DrawSourceInfo(string path)
        {
            if (path != _headerPath)
            {
                _headerPath = path;
                _headerError = null;
                _header = default;
                try
                {
                    _fileSize = new FileInfo(path).Length;
                    _header = SplatSourceReaders.ForFile(path).ReadHeader(path);
                }
                catch (Exception e)
                {
                    _headerError = e.Message;
                }
            }

            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            if (_headerError != null)
            {
                EditorGUILayout.HelpBox(_headerError, MessageType.Error);
                return;
            }
            EditorGUILayout.LabelField("File", $"{EditorUtility.FormatBytes(_fileSize)} - {_header.splatCount:N0} splats");
            EditorGUILayout.LabelField("SH Degree", _header.shDegree.ToString());
        }

        void DrawBudget()
        {
            EditorGUILayout.PropertyField(_budgetMode, s_BudgetLabel);
            if (_budgetMode.hasMultipleDifferentValues)
                return;
            EditorGUI.indentLevel++;
            switch ((GaussianSplatBudgetMode)_budgetMode.enumValueIndex)
            {
                case GaussianSplatBudgetMode.Count:
                    EditorGUILayout.PropertyField(_budgetCount, s_BudgetCountLabel);
                    break;
                case GaussianSplatBudgetMode.Percentage:
                    EditorGUILayout.PropertyField(_budgetPercent, s_BudgetPercentLabel);
                    break;
            }
            EditorGUI.indentLevel--;
        }

        // The budget and SH settings as shown, unapplied edits included
        GaussianSplatImportSettings ShownSettings()
        {
            return new GaussianSplatImportSettings
            {
                budgetMode = (GaussianSplatBudgetMode)_budgetMode.enumValueIndex,
                budgetCount = _budgetCount.intValue,
                budgetPercent = _budgetPercent.floatValue,
                maxSHDegree = _maxSHDegree.intValue,
            };
        }

        void CopyPresetToCustom()
        {
            // Entering Custom starts from the preset that was showing, taken
            // from the applied settings rather than the default Medium.
            var importer = (GaussianSplatImporter)target;
            GaussianSplatImportSettings applied = importer.Settings;
            GaussianSplatFormats from = applied.quality == GaussianSplatQuality.Custom
                ? GaussianSplatFormats.ForQuality(GaussianSplatQuality.Medium)
                : GaussianSplatFormats.ForQuality(applied.quality);
            _customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.pos)).enumValueIndex = (int)from.pos;
            _customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.scale)).enumValueIndex = (int)from.scale;
            _customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.color)).enumValueIndex = (int)from.color;
            _customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.sh)).enumValueIndex = (int)from.sh;
        }

        void DrawCustomFormats()
        {
            EditorGUILayout.PropertyField(_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.pos)), new GUIContent("Position"));
            EditorGUILayout.PropertyField(_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.scale)), new GUIContent("Scale"));
            EditorGUILayout.PropertyField(_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.color)), new GUIContent("Color"));
            var sh = _customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.sh));
            EditorGUILayout.PropertyField(sh, new GUIContent("SH"));
            if (sh.enumValueIndex >= (int)GaussianSplatAsset.SHFormat.Cluster64k)
                EditorGUILayout.HelpBox("SH clustering is slow (3-10 minutes for 6M splats).", MessageType.Info);
            if (targets.Length == 1 && _headerError == null && _header.splatCount > 0)
                DrawSizeEstimate(CurrentCustomFormats());
        }

        void DrawPresetFormats(GaussianSplatFormats formats)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.EnumPopup("Position", formats.pos);
                EditorGUILayout.EnumPopup("Scale", formats.scale);
                EditorGUILayout.EnumPopup("Color", formats.color);
                EditorGUILayout.EnumPopup("SH", formats.sh);
                EditorGUI.indentLevel--;
            }
            if (targets.Length == 1 && _headerError == null && _header.splatCount > 0)
                DrawSizeEstimate(formats);
        }

        GaussianSplatFormats CurrentCustomFormats()
        {
            return new GaussianSplatFormats
            {
                pos = (GaussianSplatAsset.VectorFormat)_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.pos)).enumValueIndex,
                scale = (GaussianSplatAsset.VectorFormat)_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.scale)).enumValueIndex,
                color = (GaussianSplatAsset.ColorFormat)_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.color)).enumValueIndex,
                sh = (GaussianSplatAsset.SHFormat)_customFormats.FindPropertyRelative(nameof(GaussianSplatFormats.sh)).enumValueIndex,
            };
        }

        void DrawSizeEstimate(GaussianSplatFormats formats)
        {
            if (_maxSHDegree.hasMultipleDifferentValues || _budgetMode.hasMultipleDifferentValues ||
                _budgetCount.hasMultipleDifferentValues || _budgetPercent.hasMultipleDifferentValues)
                return;
            GaussianSplatImportSettings shown = ShownSettings();
            int count = shown.SplatBudgetFor(_header.splatCount);
            int shDegree = shown.SHDegreeFor(_header.shDegree);
            long size = formats.CalcDataSize(count, shDegree);
            // upper bounds: pruning by opacity can only remove more
            EditorGUILayout.LabelField("Splats", $"{count:N0} ({100.0 * count / _header.splatCount:0.#}% of source)");
            EditorGUILayout.LabelField("Asset Size",
                $"{EditorUtility.FormatBytes(size)} - {(double)_fileSize / size:F2}x smaller than source, SH degree {shDegree}");
        }
    }
}
