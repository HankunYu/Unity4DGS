using GaussianSplatting.Runtime;
using Unity.Collections;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace GaussianSplatting.Editor.Import
{
    // Shared by the importers: turns a build result into import artifacts,
    // the asset as the main object and each data block as a TextAsset.
    public static class GaussianSplatImportOutput
    {
        public static GaussianSplatAsset AddAsMainObject(AssetImportContext ctx, string name, GaussianSplatBuildResult built)
        {
            GaussianSplatAsset asset = Add(ctx, name, built, "");
            ctx.SetMainObject(asset);
            return asset;
        }

        // idPrefix keeps the identifiers of several assets in one import
        // apart; the main asset uses none, so references to it stay valid
        public static GaussianSplatAsset Add(AssetImportContext ctx, string name, GaussianSplatBuildResult built, string idPrefix)
        {
            GaussianSplatAsset asset = built.asset;
            asset.name = name;
            asset.SetAssetFiles(
                AddBlock(ctx, name, idPrefix, "chk", built.chunkData),
                AddBlock(ctx, name, idPrefix, "pos", built.posData),
                AddBlock(ctx, name, idPrefix, "oth", built.otherData),
                AddBlock(ctx, name, idPrefix, "col", built.colorData),
                AddBlock(ctx, name, idPrefix, "shs", built.shData));
            ctx.AddObjectToAsset(idPrefix + "asset", asset);
            return asset;
        }

        private static TextAsset AddBlock(AssetImportContext ctx, string name, string idPrefix, string kind, NativeArray<byte> data)
        {
            if (!data.IsCreated)
                return null;
            var block = new TextAsset(data.AsReadOnlySpan()) { name = $"{name}_{kind}" };
            ctx.AddObjectToAsset(idPrefix + kind, block);
            return block;
        }
    }
}
