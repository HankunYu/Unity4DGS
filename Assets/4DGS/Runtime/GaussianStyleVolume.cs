// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Limits a renderer's style to a region: placed under a
    /// GaussianSplatRenderer, it makes the style weight vary per splat, the
    /// renderer's Style Weight times this volume's weight where the splat is.
    /// Several volumes combine by taking the strongest; with none the style
    /// covers the whole renderer. To have the region follow the camera, give
    /// the volume a Parent Constraint with the camera as its source; animate
    /// its scale to sweep the style across the scene.
    /// </summary>
    public class GaussianStyleVolume : GaussianVolume
    {
        // Must match MAX_STYLE_VOLUMES in SplatUtilities.compute
        public const int MaxPerRenderer = 4;

        [Range(0f, 1f)]
        [Tooltip("Style weight inside the volume, before the renderer's Style Weight scales it")]
        public float weight = 1f;

        protected override Color GizmoColor => Color.magenta;

        // Shape parameters for the view-data pass: x shape, y falloff, z weight
        internal Vector4 ShaderParams => new((float)shape, falloff, weight, 0f);
    }
}
