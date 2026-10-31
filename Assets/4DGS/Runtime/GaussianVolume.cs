// SPDX-License-Identifier: MIT

using System.Collections;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// A region (Box or Sphere) that weights splats by how far inside it they
    /// are: 1 inside, easing to 0 over the falloff outside, or Unbounded,
    /// weighting every splat fully. The transform places and sizes it. What
    /// the weight drives is up to the subclass.
    /// </summary>
    public abstract class GaussianVolume : MonoBehaviour
    {
        [Tooltip("Shape of the influence region")]
        public GaussianVolumeShape shape = GaussianVolumeShape.Box;

        [Range(0f, 5f)]
        [Tooltip("Falloff distance at the volume boundary, in the volume's local units. 0 = hard edge, higher = softer gradient. Unused by Unbounded.")]
        public float falloff = 0.5f;

#if UNITY_EDITOR
        // Half length in world units of the axes marking an Unbounded volume
        private const float UnboundedGizmoSize = 0.15f;
#endif

        private Vector3 _originalScale;
        private Coroutine _scaleCoroutine;

        // Gizmo tint, so the kinds of volume tell apart in the scene view
        protected abstract Color GizmoColor { get; }

        protected virtual void Awake()
        {
            _originalScale = transform.localScale;
        }

        /// <summary>
        /// Shrink the volume to zero over duration seconds.
        /// Call from UnityEvent when tracking is lost.
        /// </summary>
        public void Release(float duration = 0.1f)
        {
            if (_scaleCoroutine != null)
                StopCoroutine(_scaleCoroutine);
            _scaleCoroutine = StartCoroutine(ScaleTo(Vector3.zero, duration));
        }

        /// <summary>
        /// Restore the volume to its original scale over duration seconds.
        /// Call from UnityEvent when tracking is regained.
        /// </summary>
        public void Restore(float duration = 0.1f)
        {
            if (_originalScale == Vector3.zero)
                return;
            if (_scaleCoroutine != null)
                StopCoroutine(_scaleCoroutine);
            _scaleCoroutine = StartCoroutine(ScaleTo(_originalScale, duration));
        }

        private IEnumerator ScaleTo(Vector3 target, float duration)
        {
            Vector3 start = transform.localScale;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                transform.localScale = Vector3.Lerp(start, target, t);
                yield return null;
            }
            transform.localScale = target;
            _scaleCoroutine = null;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            var color = GizmoColor;
            color.a = 0.15f;
            if (UnityEditor.Selection.Contains(gameObject))
                color.a = 0.4f;

            Gizmos.color = color;
            switch (shape)
            {
                case GaussianVolumeShape.Box:
                    Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                    color.a *= 0.3f;
                    Gizmos.color = color;
                    Gizmos.DrawCube(Vector3.zero, Vector3.one);
                    break;
                case GaussianVolumeShape.Sphere:
                    Gizmos.DrawWireSphere(Vector3.zero, 0.5f);
                    break;
                default:
                    // No extent to show: mark the placement with unscaled axes
                    Gizmos.matrix = Matrix4x4.TRS(transform.position, transform.rotation, Vector3.one);
                    Gizmos.DrawLine(Vector3.left * UnboundedGizmoSize, Vector3.right * UnboundedGizmoSize);
                    Gizmos.DrawLine(Vector3.down * UnboundedGizmoSize, Vector3.up * UnboundedGizmoSize);
                    Gizmos.DrawLine(Vector3.back * UnboundedGizmoSize, Vector3.forward * UnboundedGizmoSize);
                    Gizmos.DrawWireSphere(Vector3.zero, UnboundedGizmoSize * 0.25f);
                    break;
            }
        }
#endif
    }
}
