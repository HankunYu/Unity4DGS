using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>A timeline asset's summary; its tracks are edited in the timeline editor window.</summary>
    [CustomEditor(typeof(GaussianTimeline))]
    public class GaussianTimelineEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var timeline = (GaussianTimeline)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("duration"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("frameRate"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.LabelField("Tracks", timeline.Tracks.Count.ToString());
            if (GUILayout.Button("Open In Timeline Editor"))
                GaussianTimelineEditorWindow.Open(timeline);
        }
    }
}
