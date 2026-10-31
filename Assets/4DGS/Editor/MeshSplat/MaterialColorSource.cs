using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Each splat takes the albedo of its material where it sits: the base
    // color times the base texture at the sample's UV (bilinear, with the
    // texture's wrap mode). Works with URP Lit (_BaseColor, _BaseMap) and
    // the built-in Standard shader (_Color, _MainTex) out of the box; other
    // shaders need their property names.
    [Serializable]
    public class MaterialColorSource : ISplatColorSource
    {
        // Colors are taken as authored (gamma space), like 3DGS colors
        [Tooltip("Material color properties, tried in order; the first one the material has is used.")]
        public string[] colorProperties = { "_BaseColor", "_Color" };
        [Tooltip("Material texture properties, tried in order; sampled at the mesh's first UV set.")]
        public string[] textureProperties = { "_BaseMap", "_MainTex" };
        [Tooltip("Color where a mesh has no material.")]
        public Color fallbackColor = Color.white;

        public void Fill(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples, float3[] colors)
        {
            using var textures = new TextureReader();
            for (int i = 0; i < samples.Count; i++)
            {
                SurfaceSample sample = samples[i];
                SurfaceMesh mesh = meshes[sample.mesh];
                Material material = mesh.MaterialAt(sample);
                Color color = material != null ? BaseColor(material) : fallbackColor;
                Texture texture = null;
                Vector2 scale = Vector2.one, offset = Vector2.zero;
                if (material != null)
                    texture = BaseTexture(material, out scale, out offset);
                if (texture != null && mesh.uvs.Length > 0)
                {
                    float2 uv = mesh.Uv(sample) * (float2)scale + (float2)offset;
                    color *= textures.Sample(texture, uv);
                }
                colors[i] = new float3(color.r, color.g, color.b);
            }
        }

        // The materials' textures, for import dependencies
        public IEnumerable<Texture> Textures(IEnumerable<Material> materials)
        {
            foreach (Material material in materials)
            {
                Texture texture = material != null ? BaseTexture(material, out _, out _) : null;
                if (texture != null)
                    yield return texture;
            }
        }

        private Color BaseColor(Material material)
        {
            foreach (string property in colorProperties)
            {
                if (material.HasColor(property))
                    return material.GetColor(property);
            }
            return Color.white;
        }

        private Texture BaseTexture(Material material, out Vector2 scale, out Vector2 offset)
        {
            foreach (string property in textureProperties)
            {
                if (!material.HasTexture(property))
                    continue;
                scale = material.GetTextureScale(property);
                offset = material.GetTextureOffset(property);
                return material.GetTexture(property);
            }
            scale = Vector2.one;
            offset = Vector2.zero;
            return null;
        }
    }
}
