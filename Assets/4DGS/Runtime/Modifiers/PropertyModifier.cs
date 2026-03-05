// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Animates splat visual properties: opacity, scale, and color tint.
    /// Does not move splats — only modifies their appearance.
    /// </summary>
    public class PropertyModifier : GaussianAnimModifier
    {
        [Range(0f, 2f)]
        [Tooltip("Opacity multiplier applied to splats in the volume")]
        public float opacityMultiplier = 1f;

        [Range(0f, 3f)]
        [Tooltip("Scale multiplier applied to splats in the volume")]
        public float scaleMultiplier = 1f;

        [Tooltip("Color tint applied to splats in the volume")]
        public Color colorTint = Color.white;

        [Range(0f, 1f)]
        [Tooltip("Blend strength for color tint (0 = no tint, 1 = full tint)")]
        public float colorBlend = 0f;

        public override int ModifierType => TypeProperty;

        public override void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3)
        {
            // p0: opacity multiplier, scale multiplier, color blend, unused
            p0 = new Vector4(opacityMultiplier, scaleMultiplier, colorBlend, 0);
            // p1: color tint RGBA
            p1 = new Vector4(colorTint.r, colorTint.g, colorTint.b, colorTint.a);
            p2 = Vector4.zero;
            p3 = Vector4.zero;
        }
    }
}
