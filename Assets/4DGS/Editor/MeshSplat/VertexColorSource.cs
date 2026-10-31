using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // The mesh's vertex colors, interpolated across each triangle, e.g.
    // lighting or AO baked into vertex colors in a DCC tool
    [Serializable]
    public class VertexColorSource : ISplatColorSource
    {
        [Tooltip("Color of meshes without vertex colors.")]
        public Color fallbackColor = Color.white;

        public void Fill(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples, float3[] colors)
        {
            for (int i = 0; i < samples.Count; i++)
            {
                SurfaceSample sample = samples[i];
                SurfaceMesh mesh = meshes[sample.mesh];
                Color color = mesh.colors.Length > 0 ? mesh.VertexColor(sample) : fallbackColor;
                colors[i] = new float3(color.r, color.g, color.b);
            }
        }
    }
}
