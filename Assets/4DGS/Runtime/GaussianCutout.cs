using System.Linq;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    [ExecuteInEditMode]
    public class GaussianCutout : MonoBehaviour
    {
        public enum Shape
        {
            Sphere = 0,
            Box = 1
        }

        [Tooltip("Shape of the cutout region")]
        public Shape shape = Shape.Sphere;
        public bool invert = false;

        public struct ShaderData // match GaussianCutoutShaderData in CS
        {
            public Matrix4x4 matrix;
            public uint typeAndFlags;
        }

        public static ShaderData GetShaderData(GaussianCutout self, Matrix4x4 rendererMatrix)
        {
            ShaderData sd = default;
            if (self && self.isActiveAndEnabled)
            {
                var tr = self.transform;
                sd.matrix = tr.worldToLocalMatrix * rendererMatrix;
                sd.typeAndFlags = ((uint)self.shape) | (self.invert ? 0x100u : 0u);
            }
            else
            {
                sd.typeAndFlags = ~0u;
            }
            return sd;
        }

#if UNITY_EDITOR
        private void OnDrawGizmos()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            var color = Color.magenta;
            color.a = 0.2f;
            if (UnityEditor.Selection.Contains(gameObject))
                color.a = 0.9f;
            else
            {
                var activeGo = UnityEditor.Selection.activeGameObject;
                var mgr = GetComponentInParent<GaussianCutoutManager>();
                if (activeGo != null && mgr != null && activeGo == mgr.gameObject)
                {
                    if (mgr.Cutouts.Contains(this))
                        color.a = 0.5f;
                }
            }

            Gizmos.color = color;
            if (shape == Shape.Sphere)
            {
                Gizmos.DrawWireSphere(Vector3.zero, 1.0f);
            }
            if (shape == Shape.Box)
            {
                Gizmos.DrawWireCube(Vector3.zero, Vector3.one * 2);
            }
        }
#endif
    }
}
