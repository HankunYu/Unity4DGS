using System;
using System.Collections.Generic;
using System.Linq;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Editor
{
    // The side panel: settings of the selected clip, marker or track, or of the timeline
    public partial class GaussianTimelineEditorWindow
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
            else if (_selectedClip != null && _selectedTrack is GaussianTimelineClipTrack clipTrack)
            {
                DrawClipDetails(clipTrack, _selectedClip);
            }
            else if (_selectedMarker != null && _selectedTrack is GaussianMarkerTrack markerTrack)
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
                EditorGUILayout.ObjectField("Asset", timeline, typeof(GaussianTimeline), false);

            EditorGUILayout.Space();
            if (player == null)
                EditorGUILayout.HelpBox("No player in the open scenes plays this timeline, so its tracks have no bindings " +
                    "and nothing previews. Add a Gaussian Timeline Player and assign this asset.", MessageType.Warning);
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

        private void DrawTrackDetails(GaussianTimelineTrack track)
        {
            EditorGUILayout.LabelField($"{GaussianTimelineStyles.KindName(track.GetType())} Track", EditorStyles.boldLabel);
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

            if (track is GaussianChannelTrack channelTrack)
                DrawChannelPicker(channelTrack, binding);

            DrawSerializedFields(TrackPath(track), "Edit Track");
        }

        private void DrawChannelPicker(GaussianChannelTrack track, Object binding)
        {
            string[] channels = GaussianTimelineTrackMenu.ChannelNames(binding);
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

        private void DrawMarkerDetails(GaussianMarkerTrack track, GaussianTimelineMarker marker)
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
                "Gaussian Marker Receiver's events for this name.", MessageType.None);
        }

        private void DrawClipDetails(GaussianTimelineClipTrack track, GaussianTimelineClip clip)
        {
            EditorGUILayout.LabelField($"{GaussianTimelineStyles.KindName(clip.GetType())} Clip", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            string clipName = EditorGUILayout.TextField("Name", clip.Name);
            double start = EditorGUILayout.DoubleField("Start (s)", clip.Start);
            double duration = EditorGUILayout.DoubleField("Duration (s)", clip.Duration);
            double end = EditorGUILayout.DoubleField("End (s)", clip.End);
            double easeIn = EditorGUILayout.DoubleField("Ease In (s)", clip.EaseIn);
            double easeOut = EditorGUILayout.DoubleField("Ease Out (s)", clip.EaseOut);
            var pre = (GaussianClipExtrapolation)EditorGUILayout.EnumPopup("Before", clip.PreExtrapolation);
            var post = (GaussianClipExtrapolation)EditorGUILayout.EnumPopup("After", clip.PostExtrapolation);
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

        // Shortcuts that fill a clip from the scene or fit it to its content
        private void DrawClipTools(GaussianTimelineClipTrack track, GaussianTimelineClip clip)
        {
            Object binding = BindingOf(track);
            switch (clip)
            {
                case GaussianChannelConstantClip constant when TryChannel(track, binding, out IGaussianTimelineTarget target, out int index):
                    if (GUILayout.Button("Value ← Scene"))
                        ReadSceneValues(() => EditTimeline("Capture Value", () => constant.Value = target.GetChannelValue(index)));
                    break;
                case GaussianChannelRampClip ramp when TryChannel(track, binding, out IGaussianTimelineTarget target, out int index):
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("From ← Scene"))
                            ReadSceneValues(() => EditTimeline("Capture Value", () => ramp.From = target.GetChannelValue(index)));
                        if (GUILayout.Button("To ← Scene"))
                            ReadSceneValues(() => EditTimeline("Capture Value", () => ramp.To = target.GetChannelValue(index)));
                    }
                    break;
                case GaussianTransformClip transformClip when binding is Transform transform:
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("From ← Scene"))
                        {
                            ReadSceneValues(() => EditTimeline("Capture Pose", () =>
                            {
                                transformClip.FromPosition = transform.localPosition;
                                transformClip.FromRotation = transform.localEulerAngles;
                                transformClip.FromScale = transform.localScale;
                            }));
                        }
                        if (GUILayout.Button("To ← Scene"))
                        {
                            ReadSceneValues(() => EditTimeline("Capture Pose", () =>
                            {
                                transformClip.ToPosition = transform.localPosition;
                                transformClip.ToRotation = transform.localEulerAngles;
                                transformClip.ToScale = transform.localScale;
                            }));
                        }
                    }
                    break;
                case GaussianModifierStateClip stateClip when binding is GaussianModifierStateMachine machine:
                    string[] states = machine.States.Select(s => s.name).ToArray();
                    if (states.Length == 0)
                    {
                        EditorGUILayout.HelpBox($"{machine.name} has no states.", MessageType.Info);
                        break;
                    }
                    EditorGUI.BeginChangeCheck();
                    int picked = EditorGUILayout.Popup("Pick State", Math.Max(0, Array.IndexOf(states, stateClip.State)), states);
                    if (EditorGUI.EndChangeCheck())
                        EditTimeline("Pick State", () => stateClip.State = states[picked]);
                    break;
                case GaussianAudioClip audioClip when audioClip.Audio != null:
                    if (GUILayout.Button("Fit To Audio"))
                        EditTimeline("Fit Clip", () => audioClip.Duration = audioClip.Audio.length);
                    break;
                case GaussianSequenceClip sequenceClip when binding is GaussianPlayer sequencePlayer && sequencePlayer.sequence != null:
                    GaussianSplatSequence sequence = sequencePlayer.sequence;
                    EditorGUILayout.LabelField("Sequence", $"{sequence.FrameCount} frames at {sequence.frameRate:0.##} fps");
                    if (GUILayout.Button("Fit To Sequence") && sequenceClip.Speed > 0f)
                    {
                        EditTimeline("Fit Clip", () => sequenceClip.Duration =
                            Math.Max(1, sequence.FrameCount - sequenceClip.StartFrame) / (sequence.frameRate * sequenceClip.Speed));
                    }
                    break;
                case GaussianAnimationClip animationClip when animationClip.Animation != null:
                    AnimationClip animation = animationClip.Animation;
                    EditorGUILayout.LabelField("Animation", $"{animation.length:0.##} s at {animation.frameRate:0.##} fps");
                    if (GUILayout.Button("Fit To Animation") && animationClip.Speed > 0f)
                    {
                        EditTimeline("Fit Clip", () => animationClip.Duration =
                            Math.Max(GaussianTimelineClip.MinDuration, (animation.length - animationClip.ClipIn) / animationClip.Speed));
                    }
                    break;
                case GaussianParticleClip when binding is GaussianParticleEmitter emitter:
                    EditorGUILayout.HelpBox($"Seeking restarts the particles {emitter.PrewarmTime:0.##} s before the new time and simulates up to it; playing on from the start gives the same particles every take.", MessageType.None);
                    break;
                case GaussianSubTimelineClip subClip when binding is GaussianTimelinePlayer child:
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Fit To Sub-Timeline") && subClip.TimeScale > 0f)
                        {
                            EditTimeline("Fit Clip", () => subClip.Duration =
                                Math.Max(GaussianTimelineClip.MinDuration, (child.Duration - subClip.ClipIn) / subClip.TimeScale));
                        }
                        if (GUILayout.Button("Open"))
                            SetPlayer(child);
                    }
                    break;
            }
        }

        // Sensible contents for a clip added on a track, from what the track is bound to
        private void InitialiseClip(GaussianTimelineClipTrack track, GaussianTimelineClip clip)
        {
            Object binding = BindingOf(track);
            ReadSceneValues(() =>
            {
                switch (clip)
                {
                    case GaussianChannelConstantClip constant when TryChannel(track, binding, out IGaussianTimelineTarget target, out int index):
                        constant.Value = target.GetChannelValue(index);
                        break;
                    case GaussianChannelRampClip ramp when TryChannel(track, binding, out IGaussianTimelineTarget target, out int index):
                        ramp.From = ramp.To = target.GetChannelValue(index);
                        break;
                    case GaussianTransformClip transformClip when binding is Transform transform:
                        transformClip.FromPosition = transformClip.ToPosition = transform.localPosition;
                        transformClip.FromRotation = transformClip.ToRotation = transform.localEulerAngles;
                        transformClip.FromScale = transformClip.ToScale = transform.localScale;
                        break;
                    case GaussianModifierStateClip stateClip when binding is GaussianModifierStateMachine machine && machine.States.Count > 0:
                        stateClip.State = machine.States[0].name;
                        break;
                    case GaussianAudioClip audioClip when binding is AudioSource source && source.clip != null:
                        audioClip.Audio = source.clip;
                        audioClip.Duration = source.clip.length;
                        break;
                    case GaussianSubTimelineClip when binding is GaussianTimelinePlayer child && child.Duration > 0.0:
                        clip.Duration = child.Duration;
                        break;
                }
            });
        }

        private static bool TryChannel(GaussianTimelineClipTrack track, Object binding, out IGaussianTimelineTarget target, out int index)
        {
            target = binding as IGaussianTimelineTarget;
            index = target != null && track is GaussianChannelTrack channelTrack
                ? GaussianChannelTrack.FindChannel(target, channelTrack.Channel)
                : -1;
            return index >= 0;
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

        private string TrackPath(GaussianTimelineTrack track)
        {
            return $"tracks.Array.data[{IndexOf(track)}]";
        }

        private static int IndexOf(GaussianTimelineClipTrack track, GaussianTimelineClip clip)
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
