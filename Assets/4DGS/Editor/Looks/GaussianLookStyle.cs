using System;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // A style as it was when a look preset was saved, and the style asset it
    // was copied from: applying the preset writes the saved values back into
    // that asset.
    [Serializable]
    internal class GaussianLookStyle
    {
        [SerializeField] private GaussianSplatStyle snapshot;
        [SerializeField] private string source;

        public GaussianLookStyle(GaussianSplatStyle snapshot, GaussianSplatStyle source)
        {
            this.snapshot = snapshot;
            SetSource(source);
        }

        public GaussianSplatStyle Snapshot => snapshot;

        // The style asset the snapshot was taken from, null once it is gone
        public GaussianSplatStyle FindSource()
        {
            if (!GlobalObjectId.TryParse(source, out GlobalObjectId id))
                return null;
            return GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) as GaussianSplatStyle;
        }

        public void SetSource(GaussianSplatStyle style)
        {
            source = GlobalObjectId.GetGlobalObjectIdSlow(style).ToString();
        }
    }
}
