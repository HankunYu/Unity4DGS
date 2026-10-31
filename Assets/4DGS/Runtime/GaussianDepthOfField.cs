// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Thin-lens depth of field for the splats this camera renders. Each splat
    /// grows by the circle it blurs into and fades to keep its light, so the
    /// blur needs no depth buffer and works the same in stereo, in Direct
    /// composite mode and under ODS. Out-of-focus splats cost fill rate in
    /// proportion to their blurred area, and a headset viewer focuses with
    /// their own eyes: meant for flat and 360 renders.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public class GaussianDepthOfField : MonoBehaviour
    {
        [Min(0.01f)]
        [Tooltip("Distance in focus, in world units. Ignored while a focus target is set")]
        public float focusDistance = 3f;

        [Tooltip("Keeps this object in focus: the focus distance follows its depth in front of the camera")]
        public Transform focusTarget;

        [Min(0f)]
        [Tooltip("Lens radius in world units: wider blurs more. Real lenses are millimetres across; a miniature look wants centimetres")]
        public float apertureRadius = 0.02f;

        [Range(1f, 64f)]
        [Tooltip("Largest blur radius in pixels. Bounds the fill rate of splats far out of focus")]
        public float maxBlurRadius = 24f;

        public float FocusDistance
        {
            get
            {
                if (focusTarget == null)
                    return focusDistance;
                Transform tr = transform;
                return Mathf.Max(Vector3.Dot(focusTarget.position - tr.position, tr.forward), 0.01f);
            }
        }

        // The camera's depth of field, if it blurs at all
        internal static GaussianDepthOfField ActiveOn(Camera cam)
        {
            return cam != null && cam.TryGetComponent(out GaussianDepthOfField dof) &&
                   dof.isActiveAndEnabled && dof.apertureRadius > 0f
                ? dof
                : null;
        }

        // _DofParams: x lens radius, y reciprocal focus distance, z largest
        // blur radius in pixels
        internal Vector4 ShaderParams => new(apertureRadius, 1f / FocusDistance, maxBlurRadius, 0f);
    }
}
