using UnityEditor;
using UnityEngine;

namespace Tide.Editor
{
    /// <summary>A timeline asset's summary; its tracks are edited in the timeline editor window.</summary>
    [CustomEditor(typeof(TideTimeline))]
    public class TideTimelineEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var timeline = (TideTimeline)target;
            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty("duration"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("frameRate"));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.LabelField("Tracks", timeline.Tracks.Count.ToString());
            if (GUILayout.Button("Open In Timeline Editor"))
                TideEditorWindow.Open(timeline);
        }
    }
}
