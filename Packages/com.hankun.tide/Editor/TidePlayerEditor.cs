using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tide.Editor
{
    /// <summary>
    /// Player settings plus its binding table, shown per track by name rather
    /// than as raw track ids, and a way into the timeline editor.
    /// </summary>
    [CustomEditor(typeof(TidePlayer))]
    public class TidePlayerEditor : UnityEditor.Editor
    {
        private SerializedProperty _timeline;
        private SerializedProperty _updateMode;
        private SerializedProperty _audioClock;
        private SerializedProperty _playOnStart;
        private SerializedProperty _wrapMode;
        private SerializedProperty _playbackSpeed;

        private void OnEnable()
        {
            _timeline = serializedObject.FindProperty("timeline");
            _updateMode = serializedObject.FindProperty("updateMode");
            _audioClock = serializedObject.FindProperty("audioClock");
            _playOnStart = serializedObject.FindProperty("playOnStart");
            _wrapMode = serializedObject.FindProperty("wrapMode");
            _playbackSpeed = serializedObject.FindProperty("playbackSpeed");
        }

        public override void OnInspectorGUI()
        {
            var player = (TidePlayer)target;

            serializedObject.Update();
            EditorGUILayout.PropertyField(_timeline);
            EditorGUILayout.PropertyField(_updateMode);
            if (_updateMode.enumValueIndex == (int)TideUpdateMode.Audio)
            {
                EditorGUILayout.PropertyField(_audioClock);
                if (_audioClock.objectReferenceValue == null)
                    EditorGUILayout.HelpBox("Audio mode needs an AudioSource to follow; without one it runs on unscaled time.",
                        MessageType.Warning);
            }
            EditorGUILayout.PropertyField(_playOnStart);
            EditorGUILayout.PropertyField(_wrapMode);
            EditorGUILayout.PropertyField(_playbackSpeed);
            serializedObject.ApplyModifiedProperties();

            if (GUILayout.Button("Open Timeline Editor"))
                TideEditorWindow.Open(player);

            if (Application.isPlaying)
                EditorGUILayout.LabelField("Time", $"{player.CurrentTime:0.000} / {player.Duration:0.###} s");

            DrawBindings(player);
        }

        private void DrawBindings(TidePlayer player)
        {
            TideTimeline timeline = player.Timeline;
            if (timeline == null)
                return;

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Bindings", EditorStyles.boldLabel);
            foreach (TideTrack track in timeline.Tracks)
            {
                if (track == null)
                    continue;
                Object binding = player.GetBinding(track);
                string label = $"{TideStyles.TrackLabel(track)} ({TideStyles.KindName(track.GetType())})";
                EditorGUI.BeginChangeCheck();
                Object newBinding = EditorGUILayout.ObjectField(label, binding, track.BindingType, true);
                if (EditorGUI.EndChangeCheck() && (newBinding == null || track.CanBind(newBinding)))
                {
                    Undo.RecordObject(player, "Bind Timeline Track");
                    player.SetBinding(track, newBinding);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(player);
                    EditorUtility.SetDirty(player);
                }
            }

            int stale = player.Bindings.Count(b => b.Target == null || timeline.FindTrack(b.TrackId) == null);
            if (stale > 0 && GUILayout.Button($"Remove {stale} Stale Binding(s)"))
            {
                Undo.RecordObject(player, "Prune Timeline Bindings");
                player.PruneBindings();
                PrefabUtility.RecordPrefabInstancePropertyModifications(player);
                EditorUtility.SetDirty(player);
            }
        }
    }
}
