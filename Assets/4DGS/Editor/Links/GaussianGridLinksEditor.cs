using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(GaussianGridLinks))]
    [CanEditMultipleObjects]
    public class GaussianGridLinksEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var links = target as GaussianGridLinks;
            if (links != null && targets.Length == 1)
            {
                if (links.Target == null)
                {
                    EditorGUILayout.HelpBox("Set the Target renderer the lattice should link to.", MessageType.Warning);
                }
                else
                {
                    string text = $"Lattice of {links.LatticePointCount:N0} points; {links.SampleCount:N0} target splats sampled\n" +
                                  "Draw the links with a style of Segment streaks";
                    GaussianMeshDriver driver = links.Target.GetComponent<GaussianMeshDriver>();
                    if (driver != null && driver.binding != null && driver.binding.meshes != null)
                        text += "\nMeshes: " + string.Join(", ", driver.binding.meshes.Select(mesh => mesh.path));
                    EditorGUILayout.HelpBox(text, MessageType.None);
                }
            }
            DrawDefaultInspector();
        }
    }
}
