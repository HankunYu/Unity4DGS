using System;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Colors the surfaces of one material from another source than the
    // default one, e.g. a textured character among uniformly colored props.
    [Serializable]
    public class MaterialColorOverride
    {
        public Material material;
        public SplatColorKind color = SplatColorKind.Material;
    }
}
