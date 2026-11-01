using System;
using UnityEngine;

namespace Tide
{
    /// <summary>
    /// How the timeline editor shows a track or clip type: its name in menus
    /// and labels, and for a track the colour of its row and clips. Without
    /// it the name comes from the type name (minus a "Tide" prefix and the
    /// "Track"/"Clip" suffix) and the colour is grey.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class TideDisplayAttribute : Attribute
    {
        public string Name { get; }
        public bool HasColor { get; }
        public Color Color { get; }

        public TideDisplayAttribute(string name)
        {
            Name = name;
        }

        public TideDisplayAttribute(string name, float r, float g, float b)
        {
            Name = name;
            HasColor = true;
            Color = new Color(r, g, b);
        }
    }
}
