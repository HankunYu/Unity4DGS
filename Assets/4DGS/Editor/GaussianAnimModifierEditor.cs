// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Custom IMGUI editors for all GaussianAnimModifier subclasses.
    /// Forces IMGUI rendering to avoid UIElements visual-tree re-entrant
    /// update errors on the same GameObject as GaussianAnimVolume.
    /// </summary>
    [CustomEditor(typeof(DissolveModifier))]
    [CanEditMultipleObjects]
    public class DissolveModifierEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }
    }

    [CustomEditor(typeof(WaveModifier))]
    [CanEditMultipleObjects]
    public class WaveModifierEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }
    }

    [CustomEditor(typeof(WarpModifier))]
    [CanEditMultipleObjects]
    public class WarpModifierEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }
    }

    [CustomEditor(typeof(PropertyModifier))]
    [CanEditMultipleObjects]
    public class PropertyModifierEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }
    }
}
