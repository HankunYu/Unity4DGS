// SPDX-License-Identifier: MIT

using System.Collections;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Defines a spatial region (Box or Sphere) that controls which splats
    /// are affected by animation modifiers. Place this on a scene object,
    /// adjust the transform to position/scale the volume, and add modifier
    /// components as siblings.
    /// </summary>
    public class GaussianAnimVolume : MonoBehaviour
    {
        private Vector3 _originalScale;
        private Coroutine _scaleCoroutine;
        public enum VolumeShape
        {
            Box = 0,
            Sphere = 1
        }

        [Tooltip("Shape of the influence region")]
        public VolumeShape shape = VolumeShape.Box;

        [Range(0f, 5f)]
        [Tooltip("Falloff distance at the volume boundary. 0 = hard edge, higher = softer gradient.")]
        public float falloff = 0.5f;

        [Range(0f, 5f)]
        [Tooltip("How long the effect lingers after the volume moves away (seconds). 0 = instant off.")]
        public float trailDuration = 0f;

        /// <summary>
        /// GPU-uploadable data matching AnimVolumeData in the compute shader.
        /// </summary>
        public struct ShaderData
        {
            public Matrix4x4 worldToLocal;  // 64 bytes
            public Matrix4x4 localToWorld;  // 64 bytes
            public Vector4 shapeParams;     // x: type(0=box,1=sphere), y: falloff, zw: reserved
            public Vector4 boundsSize;      // box half-extents from scale, or sphere radius
        }

        /// <summary>
        /// Collect all active modifier components on this GameObject.
        /// </summary>
        public GaussianAnimModifier[] GetModifiers()
        {
            return GetComponents<GaussianAnimModifier>();
        }

        /// <summary>
        /// Pack volume data for GPU upload. Uses the transform's scale as the volume size.
        /// For Box: half-extents = scale * 0.5
        /// For Sphere: radius = max component of scale * 0.5
        /// </summary>
        public ShaderData GetShaderData()
        {
            ShaderData sd = default;
            var tr = transform;
            sd.worldToLocal = tr.worldToLocalMatrix;
            sd.localToWorld = tr.localToWorldMatrix;
            sd.shapeParams = new Vector4((float)shape, falloff, trailDuration, 0);

            Vector3 scale = tr.lossyScale;
            if (shape == VolumeShape.Box)
            {
                // box half-extents: unit cube [-1,1] scaled by transform
                sd.boundsSize = new Vector4(scale.x * 0.5f, scale.y * 0.5f, scale.z * 0.5f, 0);
            }
            else
            {
                // sphere radius: use max axis
                float radius = Mathf.Max(scale.x, Mathf.Max(scale.y, scale.z)) * 0.5f;
                sd.boundsSize = new Vector4(radius, radius, radius, 0);
            }

            return sd;
        }

        /// <summary>
        /// Shrink the volume to zero over duration seconds, then optionally disable the GameObject.
        /// Trail persistence will handle the fade-out. Call from UnityEvent when tracking is lost.
        /// </summary>
        public void Release(float duration = 0.1f)
        {
            if (_scaleCoroutine != null)
                StopCoroutine(_scaleCoroutine);
            _originalScale = transform.localScale;
            _scaleCoroutine = StartCoroutine(ScaleTo(Vector3.zero, duration, true));
        }

        /// <summary>
        /// Restore the volume to its original scale over duration seconds.
        /// Call from UnityEvent when tracking is regained.
        /// </summary>
        public void Restore(float duration = 0.1f)
        {
            if (_originalScale == Vector3.zero)
                return;
            gameObject.SetActive(true);
            if (_scaleCoroutine != null)
                StopCoroutine(_scaleCoroutine);
            _scaleCoroutine = StartCoroutine(ScaleTo(_originalScale, duration, false));
        }

        private IEnumerator ScaleTo(Vector3 target, float duration, bool disableOnComplete)
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
            if (disableOnComplete)
                gameObject.SetActive(false);
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            var color = Color.cyan;
            color.a = 0.15f;
            if (UnityEditor.Selection.Contains(gameObject))
                color.a = 0.4f;

            Gizmos.color = color;
            if (shape == VolumeShape.Box)
            {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
                color.a *= 0.3f;
                Gizmos.color = color;
                Gizmos.DrawCube(Vector3.zero, Vector3.one);
            }
            else
            {
                Gizmos.DrawWireSphere(Vector3.zero, 0.5f);
            }
        }
#endif
    }
}
