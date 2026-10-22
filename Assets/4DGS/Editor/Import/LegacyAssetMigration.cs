using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GaussianSplatting.Editor.Import
{
    // One-off migration from GaussianSplatAssets made by the removed creator
    // window (.asset + .bytes) to .spz source files imported in place. Each
    // asset is decoded through the renderer's own export path, written next
    // to it as .spz with import settings matching its formats, and every
    // scene/prefab/asset reference is repointed to the imported asset.
    //
    // Remove once projects have migrated: the next asset format version
    // makes legacy assets unreadable anyway.
    public static class LegacyAssetMigration
    {
        const string kTitle = "Migrate Legacy Gaussian Splat Assets";
        const int kLegacyMainFileId = 11400000;

        [MenuItem("Tools/Gaussian Splats/Migrate Legacy Assets to SPZ")]
        static void MigrateMenu()
        {
            var paths = FindLegacyAssets();
            if (paths.Count == 0)
            {
                EditorUtility.DisplayDialog(kTitle, "No legacy GaussianSplatAssets found.", "OK");
                return;
            }
            var list = string.Join("\n", paths.Take(15)) + (paths.Count > 15 ? $"\n... and {paths.Count - 15} more" : "");
            if (!EditorUtility.DisplayDialog(kTitle,
                    $"Convert {paths.Count} legacy assets to .spz source files next to them and repoint all references?\n\n{list}",
                    "Convert", "Cancel"))
                return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            bool delete = EditorUtility.DisplayDialog(kTitle,
                "Delete the legacy .asset and .bytes files after converting? This cannot be undone outside version control.",
                "Delete", "Keep");
            Migrate(paths, delete);
        }

        // Main-asset paths of GaussianSplatAssets that are standalone .asset
        // files rather than imported from a source file.
        public static List<string> FindLegacyAssets()
        {
            return AssetDatabase.FindAssets($"t:{nameof(GaussianSplatAsset)}")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
        }

        struct Converted
        {
            public string legacyPath;
            public string legacyGuid;
            public string spzPath;
            public GaussianSplatFormats formats;
        }

        public static void Migrate(IReadOnlyList<string> legacyPaths, bool deleteLegacy)
        {
            var setup = EditorSceneManager.GetSceneManagerSetup();
            var converted = new List<Converted>();
            var log = new StringBuilder();
            try
            {
                for (int i = 0; i < legacyPaths.Count; i++)
                {
                    string path = legacyPaths[i];
                    if (EditorUtility.DisplayCancelableProgressBar(kTitle, path, (float)i / legacyPaths.Count))
                        break;
                    try
                    {
                        converted.Add(Convert(path, log));
                    }
                    catch (Exception e)
                    {
                        log.AppendLine($"FAILED {path}: {e.Message}");
                        Debug.LogException(e);
                    }
                }

                EditorUtility.DisplayProgressBar(kTitle, "Importing .spz files", 1f);
                AssetDatabase.Refresh();
                ApplyImportSettings(converted);

                EditorUtility.DisplayProgressBar(kTitle, "Repointing references", 1f);
                RepointReferences(converted, log);

                if (deleteLegacy)
                    DeleteLegacy(converted, log);
                AssetDatabase.Refresh();
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                // scenes were edited on disk; reload them
                if (setup.Length > 0)
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            Debug.Log($"Legacy splat migration: {converted.Count}/{legacyPaths.Count} converted\n{log}");
        }

        static Converted Convert(string legacyPath, StringBuilder log)
        {
            var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(legacyPath);
            string spzPath = Path.ChangeExtension(legacyPath, ".spz");
            if (File.Exists(spzPath))
                throw new IOException($"{spzPath} already exists");

            NativeArray<InputSplatData> splats = ExportLinear(asset);
            try
            {
                int degree = DetectSHDegree(splats);
                SpzWriter.Write(spzPath, splats, degree, SpzWriter.Options.Lossless);
                if (asset.cameras is { Length: > 0 })
                    WriteCameras(Path.GetDirectoryName(legacyPath), asset.cameras, log);
                log.AppendLine($"{legacyPath} -> {spzPath} ({splats.Length:N0} splats, SH degree {degree})");
            }
            finally
            {
                splats.Dispose();
            }

            return new Converted
            {
                legacyPath = legacyPath,
                legacyGuid = AssetDatabase.AssetPathToGUID(legacyPath),
                spzPath = spzPath,
                formats = new GaussianSplatFormats
                {
                    pos = asset.posFormat,
                    scale = asset.scaleFormat,
                    color = asset.colorFormat,
                    sh = asset.shFormat,
                },
            };
        }

        // Decodes the asset on the GPU with the renderer's export kernel (the
        // same decode the renderer draws with) and linearizes the result.
        static NativeArray<InputSplatData> ExportLinear(GaussianSplatAsset asset)
        {
            GameObject configGo = null;
            if (UnityEngine.Object.FindFirstObjectByType<GaussianSplatConfig>() == null)
            {
                // shaders resolve from the package when not assigned
                configGo = new GameObject("SplatMigrationConfig") { hideFlags = HideFlags.DontSave };
                configGo.AddComponent<GaussianSplatConfig>();
            }
            var go = new GameObject("SplatMigration") { hideFlags = HideFlags.DontSave };
            try
            {
                var gs = go.AddComponent<GaussianSplatRenderer>();
                gs.splatAsset = asset;
                gs.nextAsset = asset;
                gs.Update();
                if (!gs.HasValidAsset || gs.splatCount != asset.splatCount)
                    throw new InvalidOperationException("the renderer could not load the asset");

                int count = gs.splatCount;
                using var gpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, UnsafeUtility.SizeOf<InputSplatData>());
                if (!gs.EditExportData(gpuData, false))
                    throw new InvalidOperationException("GPU export failed");
                var records = new InputSplatData[count];
                gpuData.GetData(records);

                var splats = new NativeArray<InputSplatData>(records, Allocator.Persistent);
                PlySourceReader.Linearize(splats, 15);
                return splats;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
                if (configGo != null)
                    UnityEngine.Object.DestroyImmediate(configGo);
            }
        }

        // Highest band with any nonzero coefficient. Bands a source never had
        // decode to exactly zero; quantization noise keeps a band.
        static int DetectSHDegree(NativeArray<InputSplatData> splats)
        {
            int degree = 0;
            for (int i = 0; i < splats.Length && degree < 3; i++)
            {
                var s = splats[i];
                if (degree < 3 && (Any(s.sh9) || Any(s.shA) || Any(s.shB) || Any(s.shC) || Any(s.shD) || Any(s.shE) || Any(s.shF)))
                    degree = 3;
                else if (degree < 2 && (Any(s.sh4) || Any(s.sh5) || Any(s.sh6) || Any(s.sh7) || Any(s.sh8)))
                    degree = 2;
                else if (degree < 1 && (Any(s.sh1) || Any(s.sh2) || Any(s.sh3)))
                    degree = 1;
            }
            return degree;

            static bool Any(Vector3 v) => v.x != 0 || v.y != 0 || v.z != 0;
        }

        static void WriteCameras(string dir, GaussianSplatAsset.CameraInfo[] cameras, StringBuilder log)
        {
            string path = $"{dir}/{CamerasJsonReader.FileName}";
            if (File.Exists(path))
            {
                log.AppendLine($"  cameras not written: {path} already exists");
                return;
            }
            // inverse of CamerasJsonReader.Read
            var sb = new StringBuilder("[\n");
            for (int i = 0; i < cameras.Length; i++)
            {
                var c = cameras[i];
                Vector3 x = c.axisX, y = -c.axisY, z = -c.axisZ;
                sb.Append($"{{\"id\": {i}, \"img_name\": \"\", \"width\": 0, \"height\": 0, " +
                          $"\"position\": [{F(c.pos.x)}, {F(c.pos.y)}, {F(c.pos.z)}], " +
                          $"\"rotation\": [[{F(x.x)}, {F(y.x)}, {F(z.x)}], [{F(x.y)}, {F(y.y)}, {F(z.y)}], [{F(x.z)}, {F(y.z)}, {F(z.z)}]], " +
                          "\"fx\": 0, \"fy\": 0}");
                sb.Append(i + 1 < cameras.Length ? ",\n" : "\n");
            }
            sb.Append("]\n");
            File.WriteAllText(path, sb.ToString());
            log.AppendLine($"  cameras -> {path}");

            static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        static void ApplyImportSettings(List<Converted> converted)
        {
            foreach (var c in converted)
            {
                if (AssetImporter.GetAtPath(c.spzPath) is not GaussianSplatImporter importer)
                    continue;
                var settings = importer.Settings;
                settings.quality = MatchingPreset(c.formats);
                if (settings.quality == GaussianSplatQuality.Custom)
                    settings.customFormats = c.formats;
                EditorUtility.SetDirty(importer);
                importer.SaveAndReimport();
            }
        }

        static GaussianSplatQuality MatchingPreset(GaussianSplatFormats formats)
        {
            foreach (GaussianSplatQuality q in Enum.GetValues(typeof(GaussianSplatQuality)))
            {
                if (q != GaussianSplatQuality.Custom && GaussianSplatFormats.ForQuality(q).Equals(formats))
                    return q;
            }
            return GaussianSplatQuality.Custom;
        }

        // Text-serialized scenes, prefabs and assets: swap the reference to
        // the legacy main object for the imported asset's main object.
        static void RepointReferences(List<Converted> converted, StringBuilder log)
        {
            var map = new Dictionary<string, (string from, string to)>();
            foreach (var c in converted)
            {
                var imported = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(c.spzPath);
                if (imported == null || !AssetDatabase.TryGetGUIDAndLocalFileIdentifier(imported, out string guid, out long fileId))
                {
                    log.AppendLine($"  not repointed, import failed: {c.spzPath}");
                    continue;
                }
                map[c.legacyGuid] = ($"{{fileID: {kLegacyMainFileId}, guid: {c.legacyGuid}, type: 2}}",
                                     $"{{fileID: {fileId}, guid: {guid}, type: 3}}");
            }
            if (map.Count == 0)
                return;

            var files = Directory.EnumerateFiles("Assets", "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".unity") || f.EndsWith(".prefab") || f.EndsWith(".asset"));
            foreach (string file in files)
            {
                string text = File.ReadAllText(file);
                if (!text.StartsWith("%YAML"))
                    continue;
                string updated = text;
                foreach (var (guid, (from, to)) in map)
                {
                    if (!updated.Contains(guid))
                        continue;
                    updated = updated.Replace(from, to);
                    if (updated.Contains(guid))
                        log.AppendLine($"  {file} still references {guid} in another form; check it manually");
                }
                if (updated != text)
                {
                    File.WriteAllText(file, updated);
                    log.AppendLine($"  repointed references in {file}");
                }
            }
        }

        static void DeleteLegacy(List<Converted> converted, StringBuilder log)
        {
            foreach (var c in converted)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(c.legacyPath);
                var blocks = asset == null
                    ? Array.Empty<TextAsset>()
                    : new[] { asset.chunkData, asset.posData, asset.otherData, asset.colorData, asset.shData };
                foreach (var block in blocks.Where(b => b != null))
                    AssetDatabase.DeleteAsset(AssetDatabase.GetAssetPath(block));
                AssetDatabase.DeleteAsset(c.legacyPath);
            }
            log.AppendLine($"deleted {converted.Count} legacy assets and their data files");
        }
    }
}
