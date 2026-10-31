using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // One color for the whole model, e.g. for a scanned point cloud look
    [Serializable]
    public class UniformColorSource : ISplatColorSource
    {
        public Color color = new(0.82f, 0.88f, 1.0f);

        public void Fill(IReadOnlyList<SurfaceMesh> meshes, IReadOnlyList<SurfaceSample> samples, float3[] colors)
        {
            var c = new float3(color.r, color.g, color.b);
            for (int i = 0; i < colors.Length; i++)
                colors[i] = c;
        }
    }
}
