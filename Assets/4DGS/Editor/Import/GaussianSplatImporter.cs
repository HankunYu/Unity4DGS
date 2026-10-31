using System;
using System.IO;
using GaussianSplatting.Runtime;
using Unity.Collections;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace GaussianSplatting.Editor.Import
{
    // Imports .ply/.spz source files in place: the file stays untouched in
    // the project (usable by other tools), the GaussianSplatAsset and its
    // data blocks are import artifacts, and the settings live in the .meta.
    [ScriptedImporter(Version, new[] { "ply", "spz" })]
    public class GaussianSplatImporter : ScriptedImporter
    {
        // Bump whenever the builder's output for the same input changes, so
        // existing imports are redone.
        public const int Version = 3;

        [SerializeField] private GaussianSplatImportSettings settings = new();

        public GaussianSplatImportSettings Settings => settings;

        public override void OnImportAsset(AssetImportContext ctx)
        {
            string sourcePath = ctx.assetPath;
            string name = Path.GetFileNameWithoutExtension(sourcePath);

            GaussianSplatAsset.CameraInfo[] cameras = null;
            if (settings.importCameras)
            {
                // Only the sibling file: it is inside the project, so the
                // dependency below reimports when it changes.
                string camerasPath = $"{Path.GetDirectoryName(sourcePath)}/{CamerasJsonReader.FileName}";
                if (File.Exists(camerasPath))
                {
                    ctx.DependsOnSourceAsset(camerasPath);
                    cameras = CamerasJsonReader.Read(camerasPath);
                }
            }

            NativeArray<InputSplatData> splats = default;
            GaussianSplatBuildResult built = null;
            try
            {
                splats = SplatSourceReaders.ForFile(sourcePath).Read(sourcePath, out SplatSourceHeader header);
                built = GaussianSplatAssetBuilder.Build(ref splats, header.shDegree, settings, cameras);
                GaussianSplatImportOutput.AddAsMainObject(ctx, name, built);
            }
            catch (Exception e)
            {
                ctx.LogImportError($"Gaussian splat import failed: {e.Message}");
            }
            finally
            {
                if (splats.IsCreated)
                    splats.Dispose();
                built?.Dispose();
            }
        }
    }
}
