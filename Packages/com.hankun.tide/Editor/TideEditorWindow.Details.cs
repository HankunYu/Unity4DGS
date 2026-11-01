using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tide.Editor
{
    // The side panel: settings of the selected clip, marker or track, or of the timeline
    public partial class TideEditorWindow
    {
        // Fields the panel draws itself (timing, names) or that are not for editing
        private static readonly HashSet<string> OwnFields = new()
        {
            "id", "name", "muted", "clips", "markers", "channel",
            "start", "duration", "easeIn", "easeOut", "preExtrapolation", "postExtrapolation"
        };

        private Vector2 _detailsScroll;

        private void DrawDetails(Rect rect)
        {
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, 1f, rect.height), Color.black);
            GUILayout.BeginArea(new Rect(rect.x + 6f, rect.y + 4f, rect.width - 10f, rect.height - 8f));
            _detailsScroll = EditorGUILayout.BeginScrollView(_detailsScroll);
            EditorGUIUtility.labelWidth = 110f;

            if (timeline == null)
            {
                EditorGUILayout.LabelField("No timeline", EditorStyles.boldLabel);
            }
            else if (_selectedClip != null && _selectedTrack is TideClipTrack clipTrack)
            {
                DrawClipDetails(clipTrack, _selectedClip);
            }
            else if (_selectedMarker != null && _selectedTrack is TideMarkerTrack markerTrack)
            {
                DrawMarkerDetails(markerTrack, _selectedMarker);
            }
            else if (_selectedTrack != null)
            {
                DrawTrackDetails(_selectedTrack);
            }
            else
            {
                DrawTimelineDetails();
            }

            EditorGUIUtility.labelWidth = 0f;
            EditorGUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawTimelineDetails()
        {
            EditorGUILayout.LabelField(timeline.name, EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            double duration = EditorGUILayout.DoubleField("Duration (s)", timeline.Duration);
            float frameRate = EditorGUILayout.FloatField("Frame Rate", timeline.FrameRate);
            if (EditorGUI.EndChangeCheck())
            {
                EditTimeline("Edit Timeline", () =>
                {
                    timeline.Duration = duration;
                    timeline.FrameRate = frameRate;
                });
            }
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.ObjectField("Asset", timeline, typeof(TideTimeline), false);

            EditorGUILayout.Space();
            if (player == null)
                EditorGUILayout.HelpBox("No player in the open scenes plays this timeline, so its tracks have no bindings " +
                    "and nothing previews. Add a Tide Player and assign this asset.", MessageType.Warning);
            else if (!PlayerPlaysTimeline)
                EditorGUILayout.HelpBox($"{player.name} plays another timeline.", MessageType.Warning);

            EditorGUILayout.HelpBox(
                "Drop a scene object on the tracks to add a track bound to it.\n" +
                "Right-click a lane to add clips, a clip or track header for more.\n" +
                "Drag clips to move, their edges to trim (Shift: no snapping).\n" +
                "Ctrl/Alt + wheel zooms, Shift + wheel or middle-drag pans.\n" +
                "Space plays, arrows step frames, Home/End jump, F fits, Delete removes.",
                MessageType.None);
        }

        private void DrawTrackDetails(TideTrack track)
        {
            EditorGUILayout.LabelField($"{TideStyles.KindName(track.GetType())} Track", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            string trackName = EditorGUILayout.TextField("Name", track.Name);
            bool muted = EditorGUILayout.Toggle("Muted", track.Muted);
            if (EditorGUI.EndChangeCheck())
            {
                EditTimeline("Edit Track", () =>
                {
                    track.Name = trackName;
                    track.Muted = muted;
                });
            }

            Object binding = BindingOf(track);
            if (PlayerPlaysTimeline)
            {
                EditorGUI.BeginChangeCheck();
                Object newBinding = EditorGUILayout.ObjectField("Bound To", binding, track.BindingType, true);
                if (EditorGUI.EndChangeCheck() && (newBinding == null || track.CanBind(newBinding)))
                    SetBinding(track, newBinding);
                if (binding == null && !track.BindingOptional)
                    EditorGUILayout.HelpBox("Unbound: the track does nothing until it is bound.", MessageType.Info);
            }

            if (track is TideChannelTrack channelTrack)
                DrawChannelPicker(channelTrack, binding);

            DrawSerializedFields(TrackPath(track), "Edit Track");
        }

        private void DrawChannelPicker(TideChannelTrack track, Object binding)
        {
            string[] channels = TideTrackMenu.ChannelNames(binding);
            EditorGUI.BeginChangeCheck();
            string channel;
            if (channels.Length > 0)
            {
                int index = Array.IndexOf(channels, track.Channel);
                string[] options = index >= 0 ? channels : channels.Prepend($"{track.Channel} (missing)").ToArray();
                int picked = EditorGUILayout.Popup("Channel", index >= 0 ? index : 0, options);
                channel = index >= 0 ? channels[picked] : picked == 0 ? track.Channel : options[picked];
            }
            else
            {
                channel = EditorGUILayout.TextField("Channel", track.Channel);
            }
            if (EditorGUI.EndChangeCheck())
                EditTimeline("Change Channel", () => track.Channel = channel);
        }

        private void DrawMarkerDetails(TideMarkerTrack track, TideMarker marker)
        {
            EditorGUILayout.LabelField("Marker", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            string markerName = EditorGUILayout.TextField("Name", marker.Name);
            double markerTime = EditorGUILayout.DoubleField("Time (s)", marker.Time);
            if (EditorGUI.EndChangeCheck())
            {
                EditTimeline("Edit Marker", () =>
                {
                    marker.Name = markerName;
                    marker.Time = markerTime;
                    track.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
                });
            }
            EditorGUILayout.HelpBox("Playback passing the marker raises the player's OnMarker and the bound " +
                "Tide Marker Receiver's events for this name.", MessageType.None);
        }

        private void DrawClipDetails(TideClipTrack track, TideClip clip)
        {
            EditorGUILayout.LabelField($"{TideStyles.KindName(clip.GetType())} Clip", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            string clipName = EditorGUILayout.TextField("Name", clip.Name);
            double start = EditorGUILayout.DoubleField("Start (s)", clip.Start);
            double duration = EditorGUILayout.DoubleField("Duration (s)", clip.Duration);
            double end = EditorGUILayout.DoubleField("End (s)", clip.End);
            double easeIn = EditorGUILayout.DoubleField("Ease In (s)", clip.EaseIn);
            double easeOut = EditorGUILayout.DoubleField("Ease Out (s)", clip.EaseOut);
            var pre = (TideClipExtrapolation)EditorGUILayout.EnumPopup("Before", clip.PreExtrapolation);
            var post = (TideClipExtrapolation)EditorGUILayout.EnumPopup("After", clip.PostExtrapolation);
            if (EditorGUI.EndChangeCheck())
            {
                EditTimeline("Edit Clip", () =>
                {
                    clip.Name = clipName;
                    clip.Start = start;
                    clip.Duration = Math.Abs(end - clip.End) > 1e-9 ? end - start : duration;
                    clip.EaseIn = easeIn;
                    clip.EaseOut = easeOut;
                    clip.PreExtrapolation = pre;
                    clip.PostExtrapolation = post;
                    track.SortClips();
                });
            }

            EditorGUILayout.Space();
            int clipIndex = IndexOf(track, clip);
            if (clipIndex >= 0)
                DrawSerializedFields($"{TrackPath(track)}.clips.Array.data[{clipIndex}]", "Edit Clip");
            EditorGUILayout.Space();
            DrawClipTools(track, clip);
        }

        // Shortcuts that fill a clip from the scene or fit it to its content,
        // from the clip type's TideClipEditor
        private void DrawClipTools(TideClipTrack track, TideClip clip)
        {
            TideClipEditors.For(clip.GetType())?.OnToolsGUI(ClipEditorContext(track, clip));
        }

        // Sensible contents for a clip added on a track, from what the track is bound to
        private void InitialiseClip(TideClipTrack track, TideClip clip)
        {
            TideClipEditor editor = TideClipEditors.For(clip.GetType());
            if (editor != null)
            {
                TideClipEditorContext context = ClipEditorContext(track, clip);
                ReadSceneValues(() => editor.Initialise(context));
            }
        }

        private TideClipEditorContext ClipEditorContext(TideClipTrack track, TideClip clip)
        {
            return new TideClipEditorContext(track, clip, BindingOf(track), EditTimeline, ReadSceneValues, SetPlayer);
        }

        // The remaining serialized fields of a track or clip, by property path
        private void DrawSerializedFields(string path, string undoLabel)
        {
            var serialized = new SerializedObject(timeline);
            SerializedProperty property = serialized.FindProperty(path);
            if (property == null)
                return;

            SerializedProperty end = property.GetEndProperty();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren) && !SerializedProperty.EqualContents(property, end))
            {
                enterChildren = false;
                if (OwnFields.Contains(property.name))
                    continue;
                EditorGUILayout.PropertyField(property, true);
            }

            if (serialized.hasModifiedProperties)
            {
                Undo.RecordObject(timeline, undoLabel);
                serialized.ApplyModifiedProperties();
                OnTimelineChanged();
            }
        }

        private string TrackPath(TideTrack track)
        {
            return $"tracks.Array.data[{IndexOf(track)}]";
        }

        private static int IndexOf(TideClipTrack track, TideClip clip)
        {
            for (int i = 0; i < track.Clips.Count; i++)
            {
                if (track.Clips[i] == clip)
                    return i;
            }
            return -1;
        }
    }
}
