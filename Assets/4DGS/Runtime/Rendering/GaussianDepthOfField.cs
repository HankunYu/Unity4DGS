// SPDX-License-Identifier: MIT

using UnityEngine;
using Tide;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Thin-lens depth of field for the splats this camera renders. Each splat
    /// grows by the circle it blurs into and fades to keep its light, so the
    /// blur needs no depth buffer and works the same in stereo, in Direct
    /// composite mode and under ODS. Out-of-focus splats cost fill rate in
    /// proportion to their blurred area, and a headset viewer focuses with
    /// their own eyes: meant for flat and 360 renders.
    /// Timelines drive the focus distance, aperture and blur cap as channels.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Depth of Field")]
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public class GaussianDepthOfField : MonoBehaviour, ITideChannelTarget
    {
        private static readonly string[] ChannelNames = { "Focus Distance", "Aperture Radius", "Max Blur Radius" };

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

        int ITideChannelTarget.ChannelCount => ChannelNames.Length;

        string ITideChannelTarget.GetChannelName(int index) => ChannelNames[index];

        TideValueType ITideChannelTarget.GetChannelType(int index) => TideValueType.Float;

        TideValue ITideChannelTarget.GetChannelValue(int index)
        {
            return index switch
            {
                0 => focusDistance,
                1 => apertureRadius,
                _ => maxBlurRadius
            };
        }

        void ITideChannelTarget.SetChannelValue(int index, TideValue channelValue)
        {
            float value = channelValue.AsFloat;
            switch (index)
            {
                case 0:
                    focusDistance = Mathf.Max(0.01f, value);
                    break;
                case 1:
                    apertureRadius = Mathf.Max(0f, value);
                    break;
                default:
                    maxBlurRadius = Mathf.Clamp(value, 1f, 64f);
                    break;
            }
        }
    }
}
