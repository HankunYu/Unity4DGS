using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // CPU copies of textures for sampling at arbitrary UVs. Textures are read
    // back through a render texture, so they need not be marked readable,
    // and come back with their stored values (sRGB as authored).
    internal sealed class TextureReader : IDisposable
    {
        private const int MaxSize = 4096;

        private sealed class Pixels
        {
            public int width;
            public int height;
            public Color32[] data;
            public TextureWrapMode wrapU;
            public TextureWrapMode wrapV;
        }

        private readonly Dictionary<Texture, Pixels> _cache = new();

        public Color Sample(Texture texture, float2 uv)
        {
            if (!_cache.TryGetValue(texture, out Pixels pixels))
                _cache[texture] = pixels = Read(texture);

            float x = uv.x * pixels.width - 0.5f;
            float y = uv.y * pixels.height - 0.5f;
            int x0 = (int)math.floor(x), y0 = (int)math.floor(y);
            float fx = x - x0, fy = y - y0;
            Color c00 = Fetch(pixels, x0, y0), c10 = Fetch(pixels, x0 + 1, y0);
            Color c01 = Fetch(pixels, x0, y0 + 1), c11 = Fetch(pixels, x0 + 1, y0 + 1);
            return Color.Lerp(Color.Lerp(c00, c10, fx), Color.Lerp(c01, c11, fx), fy);
        }

        public void Dispose() => _cache.Clear();

        private static Color Fetch(Pixels p, int x, int y)
        {
            x = Wrap(x, p.width, p.wrapU);
            y = Wrap(y, p.height, p.wrapV);
            return p.data[y * p.width + x];
        }

        private static int Wrap(int i, int size, TextureWrapMode mode)
        {
            switch (mode)
            {
                case TextureWrapMode.Repeat:
                    return ((i % size) + size) % size;
                case TextureWrapMode.Mirror:
                {
                    int period = size * 2;
                    int m = ((i % period) + period) % period;
                    return m < size ? m : period - 1 - m;
                }
                default:
                    return math.clamp(i, 0, size - 1);
            }
        }

        private static Pixels Read(Texture texture)
        {
            int width = math.min(texture.width, MaxSize);
            int height = math.min(texture.height, MaxSize);
            var readWrite = texture.isDataSRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            RenderTexture rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, readWrite);
            RenderTexture previous = RenderTexture.active;
            var copy = new Texture2D(width, height, TextureFormat.RGBA32, false, !texture.isDataSRGB);
            try
            {
                Graphics.Blit(texture, rt);
                RenderTexture.active = rt;
                copy.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                return new Pixels
                {
                    width = width,
                    height = height,
                    data = copy.GetPixels32(),
                    wrapU = texture.wrapModeU,
                    wrapV = texture.wrapModeV,
                };
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(copy);
            }
        }
    }
}
