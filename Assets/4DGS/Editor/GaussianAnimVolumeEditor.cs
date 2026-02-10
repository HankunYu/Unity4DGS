// SPDX-License-Identifier: MIT

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Custom IMGUI editor for GaussianAnimVolume.
    /// Forces IMGUI rendering to avoid UIElements visual-tree re-entrant
    /// update errors when modifying volume properties while
    /// GaussianAnimator [ExecuteInEditMode] LateUpdate runs.
    /// </summary>
    [CustomEditor(typeof(GaussianAnimVolume))]
    [CanEditMultipleObjects]
    public class GaussianAnimVolumeEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
        }
    }
}
