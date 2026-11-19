using System.Collections.Generic;
using UnityEditor.Presets;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// A saved look of a GaussianSplatRenderer: its style volumes, particle
    /// emitters and particle links with the styles they use, and the look
    /// settings of the renderer itself. GaussianLookPresets saves and applies
    /// them; the renderer's inspector has the buttons.
    /// </summary>
    public class GaussianLookPreset : ScriptableObject
    {
        // The layers as children of an inactive root, in a prefab next to this asset
        [SerializeField] private GameObject layers;
        // Stored inside this asset
        [SerializeField] private List<GaussianLookStyle> styles = new();
        // One per look component on the renderer's own GameObject, stored inside this asset
        [SerializeField] private List<Preset> components = new();
        [SerializeField] private List<GaussianLookReference> references = new();

        internal GameObject Layers
        {
            get => layers;
            set => layers = value;
        }

        internal List<GaussianLookStyle> Styles => styles;

        internal List<Preset> Components => components;

        internal List<GaussianLookReference> References => references;
    }
}
