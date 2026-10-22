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
        public const int Version = 2;

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
                splats = SplatSourceReaders.ForFile(sourcePath).Read(sourcePath, out _);
                built = GaussianSplatAssetBuilder.Build(ref splats, settings, cameras);

                GaussianSplatAsset asset = built.asset;
                asset.name = name;
                asset.SetAssetFiles(
                    AddBlock(ctx, name, "chk", built.chunkData),
                    AddBlock(ctx, name, "pos", built.posData),
                    AddBlock(ctx, name, "oth", built.otherData),
                    AddBlock(ctx, name, "col", built.colorData),
                    AddBlock(ctx, name, "shs", built.shData));
                ctx.AddObjectToAsset("asset", asset);
                ctx.SetMainObject(asset);
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

        static TextAsset AddBlock(AssetImportContext ctx, string name, string id, NativeArray<byte> data)
        {
            if (!data.IsCreated)
                return null;
            var block = new TextAsset(data.AsReadOnlySpan()) { name = $"{name}_{id}" };
            ctx.AddObjectToAsset(id, block);
            return block;
        }
    }
}
