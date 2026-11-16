using System;
using System.Collections.Generic;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // In-memory splat assets of identical tiny splats (white, opaque, round,
    // at the origin) for components that drive every splat on the GPU
    // (GaussianParticleEmitter, GaussianParticleLinks): their buffers replace
    // the pose, size, opacity and color, so the asset only gives the renderer
    // its splat count.
    internal static class GaussianSplatTemplate
    {
        // The asset and its data files, which go into data so Destroy can
        // free them with it
        public static GaussianSplatAsset Create(string name, int count, float scale, List<TextAsset> data)
        {
            var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
            asset.name = name;
            asset.hideFlags = HideFlags.HideAndDontSave;
            asset.Initialize(count, GaussianSplatAsset.VectorFormat.Float32, GaussianSplatAsset.VectorFormat.Float32,
                GaussianSplatAsset.ColorFormat.Norm8x4, GaussianSplatAsset.SHFormat.Float32, 0,
                Vector3.one * -1000f, Vector3.one * 1000f, null);

            var pos = new byte[GaussianSplatAsset.CalcPosDataSize(count, GaussianSplatAsset.VectorFormat.Float32)];

            // Rotation (10.10.10.2 smallest three of the identity), then scale
            int otherStride = GaussianSplatAsset.GetOtherSize(GaussianSplatAsset.VectorFormat.Float32, GaussianSplatAsset.SHFormat.Float32);
            var other = new byte[(long)count * otherStride];
            uint rotation = 512u | (512u << 10) | (512u << 20) | (3u << 30);
            byte[] rotationBytes = BitConverter.GetBytes(rotation);
            byte[] scaleBytes = BitConverter.GetBytes(scale);
            for (int i = 0; i < count; i++)
            {
                int o = i * otherStride;
                Buffer.BlockCopy(rotationBytes, 0, other, o, 4);
                Buffer.BlockCopy(scaleBytes, 0, other, o + 4, 4);
                Buffer.BlockCopy(scaleBytes, 0, other, o + 8, 4);
                Buffer.BlockCopy(scaleBytes, 0, other, o + 12, 4);
            }

            var color = new byte[GaussianSplatAsset.CalcColorDataSize(count, GaussianSplatAsset.ColorFormat.Norm8x4)];
            Array.Fill(color, (byte)255);

            TextAsset posData = CreateData(name + "Pos", pos, data);
            TextAsset otherData = CreateData(name + "Other", other, data);
            TextAsset colorData = CreateData(name + "Color", color, data);
            asset.SetAssetFiles(null, posData, otherData, colorData, null);
            asset.SetDataHash(Hash128.Compute(count));
            return asset;
        }

        // Frees an asset Create made, and its data files
        public static void Destroy(GaussianSplatAsset asset, List<TextAsset> data)
        {
            DestroyObject(asset);
            foreach (TextAsset text in data)
                DestroyObject(text);
            data.Clear();
        }

        private static TextAsset CreateData(string name, byte[] bytes, List<TextAsset> data)
        {
            var text = new TextAsset(new ReadOnlySpan<byte>(bytes))
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            data.Add(text);
            return text;
        }

        private static void DestroyObject(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }
    }
}
