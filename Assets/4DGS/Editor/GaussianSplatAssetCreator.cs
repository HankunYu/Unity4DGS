// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using GaussianSplatting.Editor.Import;
using GaussianSplatting.Editor.Utils;
using GaussianSplatting.Runtime;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    public class GaussianSplatAssetCreator : EditorWindow
    {
        const string kProgressTitle = "Creating Gaussian Splat Asset";
        const string kPrefQuality = "nesnausk.GaussianSplatting.CreatorQuality";
        const string kPrefOutputFolder = "nesnausk.GaussianSplatting.CreatorOutputFolder";
        const string kPrefPruneOpacity = "nesnausk.GaussianSplatting.CreatorPruneOpacity";

        readonly FilePickerControl m_FilePicker = new();

        private enum Mode { Single, Batch }
        private Mode currentMode = Mode.Single;

        [SerializeField] string m_InputFile;
        [SerializeField] string m_InputFolder;
        [SerializeField] string m_OutputFolder = "Assets/GaussianAssets";
        [SerializeField] GaussianSplatImportSettings m_Settings = new();

        string m_ErrorMessage;
        string m_PrevFilePath;
        int m_PrevVertexCount;
        long m_PrevFileSize;

        [MenuItem("Tools/Gaussian Splats/Create GaussianSplatAsset")]
        public static void Init()
        {
            var window = GetWindowWithRect<GaussianSplatAssetCreator>(new Rect(50, 50, 360, 340), false, "Gaussian Splat Creator", true);
            window.minSize = new Vector2(320, 320);
            window.maxSize = new Vector2(1500, 1500);
            window.Show();
        }

        void Awake()
        {
            m_Settings.quality = (GaussianSplatQuality)EditorPrefs.GetInt(kPrefQuality, (int)GaussianSplatQuality.Medium);
            m_OutputFolder = EditorPrefs.GetString(kPrefOutputFolder, "Assets/GaussianAssets");
            m_Settings.pruneOpacity = EditorPrefs.GetFloat(kPrefPruneOpacity, 0f);
        }

        void OnGUI()
        {
            EditorGUILayout.Space();
            GUILayout.Label("Input data", EditorStyles.boldLabel);
            currentMode = (Mode)GUILayout.Toolbar((int)currentMode, new string[] { "Single", "Batch" });
            var rect = EditorGUILayout.GetControlRect(true);
            switch (currentMode)
            {
                case Mode.Single:
                    m_InputFile = m_FilePicker.PathFieldGUI(rect, new GUIContent("Input PLY/SPZ File"), m_InputFile, "ply,spz", "PointCloudFile");
                    break;
                case Mode.Batch:
                    m_InputFolder = m_FilePicker.PathFieldGUI(rect, new GUIContent("Input Folder"), m_InputFolder, null, "GaussianAssetInputFolder");
                    break;
            }
            m_Settings.importCameras = EditorGUILayout.Toggle("Import Cameras", m_Settings.importCameras);

            if (m_InputFile != m_PrevFilePath && !string.IsNullOrWhiteSpace(m_InputFile))
            {
                m_PrevVertexCount = 0;
                m_ErrorMessage = null;
                try
                {
                    if (File.Exists(m_InputFile))
                        m_PrevVertexCount = SplatSourceReaders.ForFile(m_InputFile).ReadHeader(m_InputFile).splatCount;
                }
                catch (Exception ex)
                {
                    m_ErrorMessage = ex.Message;
                }

                m_PrevFileSize = File.Exists(m_InputFile) ? new FileInfo(m_InputFile).Length : 0;
                m_PrevFilePath = m_InputFile;
            }

            if (m_PrevVertexCount > 0)
                EditorGUILayout.LabelField("File Size", $"{EditorUtility.FormatBytes(m_PrevFileSize)} - {m_PrevVertexCount:N0} splats");
            else
                GUILayout.Space(EditorGUIUtility.singleLineHeight);

            EditorGUILayout.Space();
            GUILayout.Label("Output", EditorStyles.boldLabel);
            rect = EditorGUILayout.GetControlRect(true);
            string newOutputFolder = m_FilePicker.PathFieldGUI(rect, new GUIContent("Output Folder"), m_OutputFolder, null, "GaussianAssetOutputFolder");
            if (newOutputFolder != m_OutputFolder)
            {
                m_OutputFolder = newOutputFolder;
                EditorPrefs.SetString(kPrefOutputFolder, m_OutputFolder);
            }

            float newPrune = EditorGUILayout.Slider(new GUIContent("Prune Opacity Below",
                "Drop splats less opaque than this at import. They still cost sorting and per-frame " +
                "view-data work. The renderer culls below ~0.02 at opacity scale 1, so values up to " +
                "that change nothing on screen unless a renderer raises its opacity scale. 0 keeps all."),
                m_Settings.pruneOpacity, 0f, 0.1f);
            if (!Mathf.Approximately(newPrune, m_Settings.pruneOpacity))
            {
                m_Settings.pruneOpacity = newPrune;
                EditorPrefs.SetFloat(kPrefPruneOpacity, m_Settings.pruneOpacity);
            }

            var newQuality = (GaussianSplatQuality)EditorGUILayout.EnumPopup("Quality", m_Settings.quality);
            if (newQuality != m_Settings.quality)
            {
                // entering Custom starts from the preset that was showing
                if (newQuality == GaussianSplatQuality.Custom)
                    m_Settings.customFormats = m_Settings.Formats;
                m_Settings.quality = newQuality;
                EditorPrefs.SetInt(kPrefQuality, (int)m_Settings.quality);
            }

            GaussianSplatFormats formats = m_Settings.Formats;
            long sizePos = 0, sizeOther = 0, sizeCol = 0, sizeSHs = 0, totalSize = 0;
            if (m_PrevVertexCount > 0)
            {
                sizePos = GaussianSplatAsset.CalcPosDataSize(m_PrevVertexCount, formats.pos);
                sizeOther = GaussianSplatAsset.CalcOtherDataSize(m_PrevVertexCount, formats.scale);
                sizeCol = GaussianSplatAsset.CalcColorDataSize(m_PrevVertexCount, formats.color);
                sizeSHs = GaussianSplatAsset.CalcSHDataSize(m_PrevVertexCount, formats.sh);
                long sizeChunk = formats.UsesChunks ? GaussianSplatAsset.CalcChunkDataSize(m_PrevVertexCount) : 0;
                totalSize = sizePos + sizeOther + sizeCol + sizeSHs + sizeChunk;
            }

            const float kSizeColWidth = 70;
            EditorGUI.BeginDisabledGroup(m_Settings.quality != GaussianSplatQuality.Custom);
            EditorGUI.indentLevel++;
            GUILayout.BeginHorizontal();
            formats.pos = (GaussianSplatAsset.VectorFormat)EditorGUILayout.EnumPopup("Position", formats.pos);
            GUILayout.Label(sizePos > 0 ? EditorUtility.FormatBytes(sizePos) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            formats.scale = (GaussianSplatAsset.VectorFormat)EditorGUILayout.EnumPopup("Scale", formats.scale);
            GUILayout.Label(sizeOther > 0 ? EditorUtility.FormatBytes(sizeOther) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            formats.color = (GaussianSplatAsset.ColorFormat)EditorGUILayout.EnumPopup("Color", formats.color);
            GUILayout.Label(sizeCol > 0 ? EditorUtility.FormatBytes(sizeCol) : string.Empty, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            formats.sh = (GaussianSplatAsset.SHFormat)EditorGUILayout.EnumPopup("SH", formats.sh);
            GUIContent shGC = new GUIContent();
            shGC.text = sizeSHs > 0 ? EditorUtility.FormatBytes(sizeSHs) : string.Empty;
            if (formats.UsesSHClusters)
            {
                shGC.tooltip = "Note that SH clustering is not fast! (3-10 minutes for 6M splats)";
                shGC.image = EditorGUIUtility.IconContent("console.warnicon.sml").image;
            }
            GUILayout.Label(shGC, GUILayout.Width(kSizeColWidth));
            GUILayout.EndHorizontal();
            EditorGUI.indentLevel--;
            EditorGUI.EndDisabledGroup();
            if (m_Settings.quality == GaussianSplatQuality.Custom)
                m_Settings.customFormats = formats;
            if (totalSize > 0)
                EditorGUILayout.LabelField("Asset Size", $"{EditorUtility.FormatBytes(totalSize)} - {(double) m_PrevFileSize / totalSize:F2}x smaller");
            else
                GUILayout.Space(EditorGUIUtility.singleLineHeight);


            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.Space(30);
            if (GUILayout.Button("Create Asset"))
            {
                CreateAsset();
            }
            GUILayout.Space(30);
            GUILayout.EndHorizontal();

            if (!string.IsNullOrWhiteSpace(m_ErrorMessage))
            {
                EditorGUILayout.HelpBox(m_ErrorMessage, MessageType.Error);
            }
        }

        static T CreateOrReplaceAsset<T>(T asset, string path) where T : UnityEngine.Object
        {
            T result = AssetDatabase.LoadAssetAtPath<T>(path);
            if (result == null)
            {
                AssetDatabase.CreateAsset(asset, path);
                result = asset;
            }
            else
            {
                if (typeof(Mesh).IsAssignableFrom(typeof(T))) { (result as Mesh)?.Clear(); }
                EditorUtility.CopySerialized(asset, result);
            }
            return result;
        }

        void CreateAsset()
        {
            switch (currentMode)
            {
                case Mode.Single:
                    CreateAssetSingle();
                    break;
                case Mode.Batch:
                    if (!Directory.Exists(m_InputFolder))
                    {
                        Debug.LogWarning("Invalid folder path: " + m_InputFolder);
                        break;
                    }

                    var plyFiles = Directory.GetFiles(m_InputFolder, "*.ply", SearchOption.TopDirectoryOnly);
                    var spzFiles = Directory.GetFiles(m_InputFolder, "*.spz", SearchOption.TopDirectoryOnly);
                    var inputFiles = new string[plyFiles.Length + spzFiles.Length];
                    plyFiles.CopyTo(inputFiles, 0);
                    spzFiles.CopyTo(inputFiles, plyFiles.Length);

                    if (inputFiles.Length == 0)
                    {
                        Debug.LogWarning("No PLY or SPZ files found in: " + m_InputFolder);
                        break;
                    }

                    Debug.Log($"Batch import: found {inputFiles.Length} files in {m_InputFolder}");
                    // Each import resets m_ErrorMessage; collect them so an
                    // early failure is not hidden by later successes.
                    var failures = new List<string>();
                    foreach (string file in inputFiles)
                    {
                        m_InputFile = file;
                        CreateAssetSingle();
                        if (!string.IsNullOrEmpty(m_ErrorMessage))
                            failures.Add(m_ErrorMessage);
                    }
                    m_ErrorMessage = failures.Count > 0 ? string.Join("\n", failures) : null;
                    break;
            }
        }

        void CreateAssetSingle()
        {
            m_ErrorMessage = null;
            if (string.IsNullOrWhiteSpace(m_InputFile))
            {
                m_ErrorMessage = $"Select input PLY/SPZ file";
                return;
            }
            if (!File.Exists(m_InputFile))
            {
                m_ErrorMessage = $"Did not find {m_InputFile} file";
                return;
            }

            if (string.IsNullOrWhiteSpace(m_OutputFolder) || !m_OutputFolder.StartsWith("Assets/"))
            {
                m_ErrorMessage = $"Output folder must be within project, was '{m_OutputFolder}'";
                return;
            }
            Directory.CreateDirectory(m_OutputFolder);

            // Everything below allocates native memory and holds a modal
            // progress bar: any failure must still release both, and report
            // which file failed instead of leaving the editor stuck.
            NativeArray<InputSplatData> inputSplats = default;
            GaussianSplatBuildResult built = null;
            try
            {
                EditorUtility.DisplayProgressBar(kProgressTitle, "Reading data files", 0.0f);
                GaussianSplatAsset.CameraInfo[] cameras = m_Settings.importCameras ? CamerasJsonReader.FindAndRead(m_InputFile) : null;
                inputSplats = SplatSourceReaders.ForFile(m_InputFile).Read(m_InputFile, out _);

                built = GaussianSplatAssetBuilder.Build(ref inputSplats, m_Settings, cameras,
                    (stage, progress) => EditorUtility.DisplayProgressBar(kProgressTitle, stage, progress));
                GaussianSplatAsset asset = built.asset;

                string baseName = Path.GetFileNameWithoutExtension(FilePickerControl.PathToDisplayString(m_InputFile));
                asset.name = baseName;
                string pathChunk = $"{m_OutputFolder}/{baseName}_chk.bytes";
                string pathPos = $"{m_OutputFolder}/{baseName}_pos.bytes";
                string pathOther = $"{m_OutputFolder}/{baseName}_oth.bytes";
                string pathCol = $"{m_OutputFolder}/{baseName}_col.bytes";
                string pathSh = $"{m_OutputFolder}/{baseName}_shs.bytes";

                bool useChunks = built.chunkData.IsCreated;
                if (useChunks)
                    WriteBlock(pathChunk, built.chunkData);
                WriteBlock(pathPos, built.posData);
                WriteBlock(pathOther, built.otherData);
                WriteBlock(pathCol, built.colorData);
                WriteBlock(pathSh, built.shData);

                // files are created, import them so we can get to the imported objects, ugh
                EditorUtility.DisplayProgressBar(kProgressTitle, "Initial texture import", 0.85f);
                AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);

                EditorUtility.DisplayProgressBar(kProgressTitle, "Setup data onto asset", 0.95f);
                asset.SetAssetFiles(
                    useChunks ? AssetDatabase.LoadAssetAtPath<TextAsset>(pathChunk) : null,
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathPos),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathOther),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathCol),
                    AssetDatabase.LoadAssetAtPath<TextAsset>(pathSh));

                var assetPath = $"{m_OutputFolder}/{baseName}.asset";
                var savedAsset = CreateOrReplaceAsset(asset, assetPath);

                EditorUtility.DisplayProgressBar(kProgressTitle, "Saving assets", 0.99f);
                AssetDatabase.SaveAssets();

                Selection.activeObject = savedAsset;
            }
            catch (Exception e)
            {
                m_ErrorMessage = $"Failed to import {m_InputFile}: {e.Message}";
                Debug.LogException(e);
            }
            finally
            {
                if (inputSplats.IsCreated)
                    inputSplats.Dispose();
                built?.Dispose();
                EditorUtility.ClearProgressBar();
            }
        }

        static void WriteBlock(string path, NativeArray<byte> data)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            fs.Write(data);
        }
    }
}
