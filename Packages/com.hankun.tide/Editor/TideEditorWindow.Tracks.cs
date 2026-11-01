using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tide.Editor
{
    // Toolbar, ruler, track rows and the mouse and keyboard on them
    public partial class TideEditorWindow
    {
        private const float HeaderWidth = 240f;
        private const float DetailsWidth = 300f;
        private const float RulerHeight = 22f;
        private const float TrackHeight = 40f;
        private const float GroupHeight = 20f;
        private const float ScrollbarWidth = 14f;
        private const float EdgeGrab = 6f;
        private const float MarkerGrab = 7f;
        private const float SnapPixels = 8f;
        private const float MinPixelsPerSecond = 2f;
        private const float MaxPixelsPerSecond = 4000f;
        private const float MinTickSpacing = 70f;

        private static readonly double[] TickSteps = { 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600 };

        private enum DragMode
        {
            None,
            Scrub,
            MoveClip,
            TrimStart,
            TrimEnd,
            MoveMarker,
            Pan
        }

        private sealed class Row
        {
            public bool IsGroup;
            // Group rows: the GameObject the group's tracks drive (null = unbound)
            public GameObject Group;
            public TideTrack Track;
            public float Y;
            public float Height;
        }

        private readonly List<Row> _rows = new();
        private readonly HashSet<GameObject> _collapsed = new();
        private bool _unboundCollapsed;
        private TideTrack _selectedTrack;
        private TideClip _selectedClip;
        private TideMarker _selectedMarker;
        private DragMode _drag;
        private double _dragGrab;
        private double _dragFixedEnd;
        private int _dragUndoGroup;
        private Vector2 _scroll;
        private float _timelineWidth;

        private void OnGUI()
        {
            int dragControl = GUIUtility.GetControlID(FocusType.Passive);
            float top = EditorStyles.toolbar.fixedHeight;
            DrawToolbar();

            var details = new Rect(position.width - DetailsWidth, top, DetailsWidth, position.height - top);
            var main = new Rect(0f, top, position.width - DetailsWidth, position.height - top);
            EditorGUI.DrawRect(main, TideStyles.Background);
            DrawDetails(details);

            if (timeline == null)
            {
                var help = new Rect(main.x + 20f, main.y + 20f, main.width - 40f, 60f);
                EditorGUI.HelpBox(help, "Select a GameObject with a Tide Player, or a Tide Timeline asset. " +
                    "Create one with New in the toolbar.", MessageType.Info);
                return;
            }

            BuildRows();
            float contentHeight = _rows.Count > 0 ? _rows[^1].Y + _rows[^1].Height : 0f;
            var rowsView = new Rect(0f, top + RulerHeight, main.width, main.height - RulerHeight);
            bool scrollbar = contentHeight > rowsView.height;
            float contentWidth = rowsView.width - (scrollbar ? ScrollbarWidth : 0f);
            _timelineWidth = Mathf.Max(10f, contentWidth - HeaderWidth);

            HandleWheel(rowsView);
            DrawRuler(new Rect(HeaderWidth, top, _timelineWidth, RulerHeight), dragControl);
            DrawHeaderCorner(new Rect(0f, top, HeaderWidth, RulerHeight));

            _scroll = GUI.BeginScrollView(rowsView, _scroll, new Rect(0f, 0f, contentWidth, contentHeight), false, scrollbar);
            DrawRows(contentWidth, Mathf.Max(contentHeight, rowsView.height));
            HandleRowMouse(dragControl, contentWidth);
            HandleDragAndDrop(new Rect(0f, 0f, contentWidth, Mathf.Max(contentHeight, rowsView.height)));
            GUI.EndScrollView();

            DrawPlayhead(new Rect(HeaderWidth, top, _timelineWidth, main.height));
            HandleDrag(dragControl);
            HandleKeys();
        }

        // ------------------------------------------------------------------ toolbar

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                var newPlayer = (TidePlayer)EditorGUILayout.ObjectField(player, typeof(TidePlayer), true,
                    GUILayout.Width(170f));
                if (newPlayer != player)
                    SetPlayer(newPlayer);

                var newTimeline = (TideTimeline)EditorGUILayout.ObjectField(timeline, typeof(TideTimeline), false,
                    GUILayout.Width(170f));
                if (newTimeline != timeline)
                    AssignTimeline(newTimeline);

                if (GUILayout.Button("New", EditorStyles.toolbarButton, GUILayout.Width(40f)))
                    CreateTimeline();

                GUILayout.Space(8f);
                if (GUILayout.Button(EditorGUIUtility.IconContent("Animation.FirstKey"), EditorStyles.toolbarButton, GUILayout.Width(28f)))
                    SetTime(0.0);
                if (GUILayout.Button(EditorGUIUtility.IconContent(IsPlaying ? "PauseButton" : "PlayButton"), EditorStyles.toolbarButton,
                        GUILayout.Width(28f)))
                    TogglePlayback();
                if (GUILayout.Button(EditorGUIUtility.IconContent("Animation.LastKey"), EditorStyles.toolbarButton, GUILayout.Width(28f)))
                    SetTime(Duration);

                EditorGUI.BeginChangeCheck();
                double newTime = EditorGUILayout.DoubleField(Math.Round(time, 3), EditorStyles.toolbarTextField, GUILayout.Width(60f));
                if (EditorGUI.EndChangeCheck())
                    SetTime(newTime);
                GUILayout.Label($"s  f{Math.Round(time / FrameDuration)}", EditorStyles.miniLabel, GUILayout.Width(60f));

                using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                {
                    EditorGUI.BeginChangeCheck();
                    previewEnabled = GUILayout.Toggle(previewEnabled, "Preview", EditorStyles.toolbarButton, GUILayout.Width(60f));
                    if (EditorGUI.EndChangeCheck())
                        ApplyPreview();
                }

                using (new EditorGUI.DisabledScope(timeline == null))
                {
                    if (EditorGUILayout.DropdownButton(new GUIContent("Add Track"), FocusType.Passive, EditorStyles.toolbarDropDown,
                            GUILayout.Width(80f)))
                        ShowAddTrackMenu();
                }

                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Fit", EditorStyles.toolbarButton, GUILayout.Width(32f)))
                    FitView();
            }
        }

        private void AssignTimeline(TideTimeline asset)
        {
            if (player == null)
            {
                SetTimeline(asset);
                return;
            }
            StopPlayback();
            EndPreview();
            Undo.RecordObject(player, "Assign Timeline");
            player.Timeline = asset;
            PrefabUtility.RecordPrefabInstancePropertyModifications(player);
            EditorUtility.SetDirty(player);
            timeline = asset;
            ClearSelection();
            ApplyPreview();
        }

        private void CreateTimeline()
        {
            string folder = timeline != null
                ? System.IO.Path.GetDirectoryName(AssetDatabase.GetAssetPath(timeline))
                : "Assets";
            string name = player != null ? $"{player.gameObject.scene.name} {player.name}" : "Timeline";
            string path = EditorUtility.SaveFilePanelInProject("New Tide Timeline", name, "asset", "", folder);
            if (string.IsNullOrEmpty(path))
                return;
            var asset = CreateInstance<TideTimeline>();
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssignTimeline(asset);
        }

        private void ShowAddTrackMenu()
        {
            var menu = new GenericMenu();
            foreach (Type type in TideTrackMenu.TrackTypes())
            {
                Type trackType = type;
                menu.AddItem(new GUIContent(TideStyles.KindName(type)), false, () =>
                {
                    var track = (TideTrack)Activator.CreateInstance(trackType);
                    EditTimeline("Add Track", () => timeline.AddTrack(track));
                    SelectTrack(track);
                });
            }
            menu.ShowAsContext();
        }

        // ------------------------------------------------------------------ rows

        private void BuildRows()
        {
            _rows.Clear();
            var groups = new List<GameObject>();
            var tracksByGroup = new Dictionary<GameObject, List<TideTrack>>();
            var unbound = new List<TideTrack>();
            foreach (TideTrack track in timeline.Tracks)
            {
                if (track == null)
                    continue;
                GameObject group = BoundGameObject(track);
                if (group == null)
                {
                    unbound.Add(track);
                    continue;
                }
                if (!tracksByGroup.TryGetValue(group, out List<TideTrack> tracks))
                {
                    tracks = new List<TideTrack>();
                    tracksByGroup.Add(group, tracks);
                    groups.Add(group);
                }
                tracks.Add(track);
            }

            float y = 0f;
            foreach (GameObject group in groups)
                AddGroup(group, tracksByGroup[group], _collapsed.Contains(group), ref y);
            if (unbound.Count > 0)
                AddGroup(null, unbound, _unboundCollapsed, ref y);
        }

        private void AddGroup(GameObject group, List<TideTrack> tracks, bool collapsed, ref float y)
        {
            _rows.Add(new Row { IsGroup = true, Group = group, Y = y, Height = GroupHeight });
            y += GroupHeight;
            if (collapsed)
                return;
            foreach (TideTrack track in tracks)
            {
                _rows.Add(new Row { Track = track, Group = group, Y = y, Height = TrackHeight });
                y += TrackHeight;
            }
        }

        private Object BindingOf(TideTrack track)
        {
            return PlayerPlaysTimeline ? player.GetBinding(track) : null;
        }

        private GameObject BoundGameObject(TideTrack track)
        {
            return BindingOf(track) switch
            {
                GameObject gameObject => gameObject,
                Component component => component.gameObject,
                _ => null
            };
        }

        private void DrawRows(float contentWidth, float contentHeight)
        {
            var timelineArea = new Rect(HeaderWidth, 0f, contentWidth - HeaderWidth, contentHeight);
            if (Event.current.type == EventType.Repaint)
                DrawOutOfRange(timelineArea);

            for (int i = 0; i < _rows.Count; i++)
            {
                Row row = _rows[i];
                var header = new Rect(0f, row.Y, HeaderWidth, row.Height);
                var lane = new Rect(HeaderWidth, row.Y, contentWidth - HeaderWidth, row.Height);
                if (row.IsGroup)
                {
                    DrawGroupRow(row, header, lane);
                    continue;
                }

                bool selected = row.Track == _selectedTrack;
                if (Event.current.type == EventType.Repaint)
                {
                    Color background = selected ? TideStyles.RowSelected
                        : i % 2 == 0 ? TideStyles.RowEven : TideStyles.RowOdd;
                    EditorGUI.DrawRect(header, background);
                    EditorGUI.DrawRect(new Rect(lane.x, lane.y, lane.width, lane.height - 1f), background * 0.85f);
                    EditorGUI.DrawRect(new Rect(header.x, header.y, 4f, header.height - 1f), TideStyles.TrackColor(row.Track));
                }
                DrawTrackHeader(row.Track, header);
                DrawLane(row.Track, lane);
            }
        }

        private void DrawGroupRow(Row row, Rect header, Rect lane)
        {
            if (Event.current.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(header, TideStyles.GroupRow);
                EditorGUI.DrawRect(lane, TideStyles.GroupRow);
            }
            bool collapsed = row.Group != null ? _collapsed.Contains(row.Group) : _unboundCollapsed;
            var foldoutRect = new Rect(header.x + 4f, header.y + 2f, 14f, header.height - 4f);
            bool expanded = EditorGUI.Foldout(foldoutRect, !collapsed, GUIContent.none, true);
            if (expanded == collapsed)
            {
                if (row.Group == null)
                    _unboundCollapsed = !expanded;
                else if (expanded)
                    _collapsed.Remove(row.Group);
                else
                    _collapsed.Add(row.Group);
            }

            var labelRect = new Rect(header.x + 18f, header.y + 2f, header.width - 22f, header.height - 4f);
            GUI.Label(labelRect, row.Group != null ? row.Group.name : "Unbound", EditorStyles.boldLabel);
            Event e = Event.current;
            if (row.Group != null && e.type == EventType.MouseDown && e.button == 0 && labelRect.Contains(e.mousePosition))
            {
                Selection.activeGameObject = row.Group;
                EditorGUIUtility.PingObject(row.Group);
                e.Use();
            }
        }

        private void DrawTrackHeader(TideTrack track, Rect rect)
        {
            var nameRect = new Rect(rect.x + 10f, rect.y + 2f, rect.width - 40f, 18f);
            var muteRect = new Rect(rect.xMax - 26f, rect.y + 3f, 22f, 16f);
            var bindingRect = new Rect(rect.x + 10f, rect.y + 20f, rect.width - 16f, 16f);

            var nameStyle = new GUIStyle(EditorStyles.boldLabel);
            if (track.Muted)
                nameStyle.normal.textColor = TideStyles.Muted;
            string kind = TideStyles.KindName(track.GetType());
            GUI.Label(nameRect, new GUIContent($"{TideStyles.TrackLabel(track)}  <size=9>{kind}</size>", kind),
                new GUIStyle(nameStyle) { richText = true });

            EditorGUI.BeginChangeCheck();
            bool muted = GUI.Toggle(muteRect, track.Muted, new GUIContent("M", "Mute"), EditorStyles.miniButton);
            if (EditorGUI.EndChangeCheck())
                EditTimeline(muted ? "Mute Track" : "Unmute Track", () => track.Muted = muted);

            if (PlayerPlaysTimeline)
            {
                Object binding = player.GetBinding(track);
                EditorGUI.BeginChangeCheck();
                Object newBinding = EditorGUI.ObjectField(bindingRect, binding, track.BindingType, true);
                if (EditorGUI.EndChangeCheck() && (newBinding == null || track.CanBind(newBinding)))
                    SetBinding(track, newBinding);
            }
            else
            {
                GUI.Label(bindingRect, "No player plays this timeline", EditorStyles.miniLabel);
            }

            Event e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                SelectTrack(track);
                if (e.button == 1)
                    ShowTrackMenu(track);
                e.Use();
            }
        }

        // ------------------------------------------------------------------ lanes

        private void DrawLane(TideTrack track, Rect lane)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            Color color = TideStyles.TrackColor(track);
            if (track.Muted)
                color = Color.Lerp(color, TideStyles.Muted, 0.7f);

            if (track is TideClipTrack clipTrack)
            {
                Vector2 range = ChannelRange(clipTrack);
                IReadOnlyList<TideClip> clips = clipTrack.Clips;
                for (int i = 0; i < clips.Count; i++)
                {
                    if (clips[i] == null)
                        continue;
                    double gapEnd = i + 1 < clips.Count && clips[i + 1] != null ? clips[i + 1].Start : double.PositiveInfinity;
                    DrawClip(clips[i], ClipRect(clips[i], lane), lane, color, gapEnd, range);
                }
            }
            else if (track is TideMarkerTrack markerTrack)
            {
                foreach (TideMarker marker in markerTrack.Markers)
                    DrawMarker(marker, lane, color);
            }
        }

        private void DrawClip(TideClip clip, Rect rect, Rect lane, Color color, double gapEnd, Vector2 channelRange)
        {
            if (rect.xMax < lane.x || rect.x > lane.xMax)
                return;

            // Extrapolation: a thin bar into the gap it fills
            if (clip.PostExtrapolation != TideClipExtrapolation.None)
            {
                float end = double.IsPositiveInfinity(gapEnd) ? lane.xMax : TimeToX(gapEnd);
                EditorGUI.DrawRect(new Rect(rect.xMax, rect.center.y - 2f, Mathf.Max(0f, end - rect.xMax), 4f), color * 0.6f);
            }
            if (clip.PreExtrapolation != TideClipExtrapolation.None)
                EditorGUI.DrawRect(new Rect(lane.x, rect.center.y - 2f, Mathf.Max(0f, rect.x - lane.x), 4f), color * 0.45f);

            bool selected = clip == _selectedClip;
            EditorGUI.DrawRect(rect, color);
            float easeIn = (float)(clip.EaseIn * pixelsPerSecond);
            float easeOut = (float)(clip.EaseOut * pixelsPerSecond);
            Handles.color = TideStyles.Ease;
            if (easeIn > 1f)
                Handles.DrawAAConvexPolygon(new Vector3(rect.x, rect.y), new Vector3(rect.x + easeIn, rect.y), new Vector3(rect.x, rect.yMax));
            if (easeOut > 1f)
                Handles.DrawAAConvexPolygon(new Vector3(rect.xMax - easeOut, rect.y), new Vector3(rect.xMax, rect.y), new Vector3(rect.xMax, rect.yMax));

            if (clip is TideChannelClip channelClip)
                DrawChannelCurve(channelClip, rect, channelRange);

            Color outline = selected ? TideStyles.ClipSelected : TideStyles.ClipOutline;
            Handles.color = outline;
            Handles.DrawSolidRectangleWithOutline(rect, Color.clear, outline);
            if (selected)
                Handles.DrawSolidRectangleWithOutline(new Rect(rect.x + 1f, rect.y + 1f, rect.width - 2f, rect.height - 2f), Color.clear, outline);

            var labelRect = new Rect(rect.x + 4f, rect.y + 1f, Mathf.Max(0f, rect.width - 8f), 16f);
            GUI.Label(labelRect, clip.DisplayName, EditorStyles.whiteMiniLabel);
        }

        private void DrawChannelCurve(TideChannelClip clip, Rect rect, Vector2 range)
        {
            const int Steps = 32;
            var points = new Vector3[Steps + 1];
            float span = Mathf.Max(range.y - range.x, 1e-5f);
            for (int s = 0; s <= Steps; s++)
            {
                double local = clip.Duration * s / Steps;
                float v = (clip.Evaluate(local) - range.x) / span;
                points[s] = new Vector3(rect.x + rect.width * s / Steps, rect.yMax - 4f - v * (rect.height - 16f));
            }
            Handles.color = new Color(1f, 1f, 1f, 0.8f);
            Handles.DrawAAPolyLine(2f, points);
        }

        // Value range of a channel track's clips, for drawing their curves on one scale
        private static Vector2 ChannelRange(TideClipTrack track)
        {
            float min = float.PositiveInfinity;
            float max = float.NegativeInfinity;
            foreach (TideClip clip in track.Clips)
            {
                if (clip is not TideChannelClip channelClip)
                    continue;
                for (int s = 0; s <= 8; s++)
                {
                    float v = channelClip.Evaluate(clip.Duration * s / 8);
                    min = Mathf.Min(min, v);
                    max = Mathf.Max(max, v);
                }
            }
            if (float.IsInfinity(min))
                return new Vector2(0f, 1f);
            if (max - min < 1e-4f)
                return new Vector2(min - 0.5f, max + 0.5f);
            return new Vector2(min, max);
        }

        private void DrawMarker(TideMarker marker, Rect lane, Color color)
        {
            float x = TimeToX(marker.Time);
            if (x < lane.x - 10f || x > lane.xMax + 10f)
                return;
            float cy = lane.y + 12f;
            Handles.color = marker == _selectedMarker ? Color.white : color;
            Handles.DrawAAConvexPolygon(new Vector3(x, cy - 6f), new Vector3(x + 6f, cy), new Vector3(x, cy + 6f), new Vector3(x - 6f, cy));
            EditorGUI.DrawRect(new Rect(x, cy + 6f, 1f, lane.yMax - cy - 6f), color * 0.7f);
            GUI.Label(new Rect(x + 8f, cy - 8f, 160f, 16f), marker.Name, EditorStyles.whiteMiniLabel);
        }

        private void DrawOutOfRange(Rect area)
        {
            float end = TimeToX(Duration);
            if (end < area.xMax)
                EditorGUI.DrawRect(new Rect(Mathf.Max(end, area.x), area.y, area.xMax - Mathf.Max(end, area.x), area.height),
                    TideStyles.OutOfRange);
            if (end >= area.x && end <= area.xMax)
                EditorGUI.DrawRect(new Rect(end, area.y, 1f, area.height), TideStyles.EndLine);
        }

        // ------------------------------------------------------------------ ruler + playhead

        private void DrawHeaderCorner(Rect rect)
        {
            EditorGUI.DrawRect(rect, TideStyles.Ruler);
            string label = PlayerPlaysTimeline ? player.name : timeline.name;
            GUI.Label(new Rect(rect.x + 6f, rect.y + 3f, rect.width - 12f, rect.height - 6f), label, EditorStyles.miniLabel);
        }

        private void DrawRuler(Rect rect, int dragControl)
        {
            Event e = Event.current;
            if (e.type == EventType.Repaint)
            {
                EditorGUI.DrawRect(rect, TideStyles.Ruler);
                double step = TickSteps.FirstOrDefault(s => s * pixelsPerSecond >= MinTickSpacing);
                if (step <= 0.0)
                    step = TickSteps[^1];
                double minor = step / 5.0;
                double first = Math.Floor(viewStart / minor) * minor;
                double last = XToTime(rect.xMax);
                for (double t = first; t <= last; t += minor)
                {
                    float x = TimeToX(t);
                    if (x < rect.x)
                        continue;
                    bool major = Math.Abs(t / step - Math.Round(t / step)) < 1e-6;
                    float height = major ? rect.height * 0.5f : rect.height * 0.2f;
                    EditorGUI.DrawRect(new Rect(x, rect.yMax - height, 1f, height), TideStyles.Tick);
                    if (major)
                        GUI.Label(new Rect(x + 3f, rect.y, 70f, 14f), FormatTime(t, step), EditorStyles.miniLabel);
                }
                float end = TimeToX(Duration);
                if (end >= rect.x && end <= rect.xMax)
                    EditorGUI.DrawRect(new Rect(end, rect.y, 1f, rect.height), TideStyles.EndLine);
            }

            if (e.type == EventType.MouseDown && e.button == 0 && rect.Contains(e.mousePosition))
            {
                GUIUtility.hotControl = dragControl;
                _drag = DragMode.Scrub;
                StopPlayback();
                SetTime(Snap(XToTime(e.mousePosition.x), !e.shift));
                e.Use();
            }
        }

        private static string FormatTime(double t, double step)
        {
            if (t >= 60.0)
                return $"{(int)(t / 60.0)}:{t % 60.0:00}";
            return step < 1.0 ? t.ToString("0.##") : t.ToString("0");
        }

        private void DrawPlayhead(Rect rect)
        {
            if (Event.current.type != EventType.Repaint)
                return;
            float x = TimeToX(time);
            if (x < rect.x || x > rect.xMax)
                return;
            EditorGUI.DrawRect(new Rect(x, rect.y, 1f, rect.height), TideStyles.Playhead);
            Handles.color = TideStyles.Playhead;
            Handles.DrawAAConvexPolygon(new Vector3(x - 5f, rect.y), new Vector3(x + 6f, rect.y), new Vector3(x + 0.5f, rect.y + 8f));
        }

        // ------------------------------------------------------------------ mouse

        private void HandleWheel(Rect rowsView)
        {
            Event e = Event.current;
            if (e.type != EventType.ScrollWheel)
                return;
            var area = new Rect(HeaderWidth, rowsView.y - RulerHeight, _timelineWidth, rowsView.height + RulerHeight);
            if (!area.Contains(e.mousePosition))
                return;

            if (e.control || e.command || e.alt)
            {
                double anchor = XToTime(e.mousePosition.x);
                float factor = e.delta.y > 0f ? 1f / 1.15f : 1.15f;
                pixelsPerSecond = Mathf.Clamp(pixelsPerSecond * factor, MinPixelsPerSecond, MaxPixelsPerSecond);
                viewStart = Math.Max(0.0, anchor - (e.mousePosition.x - HeaderWidth) / pixelsPerSecond);
                e.Use();
                Repaint();
            }
            else if (e.shift)
            {
                viewStart = Math.Max(0.0, viewStart + e.delta.y * 20f / pixelsPerSecond);
                e.Use();
                Repaint();
            }
        }

        // Mouse down on the rows (in scroll-view coordinates)
        private void HandleRowMouse(int dragControl, float contentWidth)
        {
            Event e = Event.current;
            if (e.type != EventType.MouseDown || e.mousePosition.x < HeaderWidth || e.mousePosition.x > contentWidth)
                return;

            if (e.button == 2)
            {
                GUIUtility.hotControl = dragControl;
                _drag = DragMode.Pan;
                e.Use();
                return;
            }

            Row row = _rows.FirstOrDefault(r => !r.IsGroup && e.mousePosition.y >= r.Y && e.mousePosition.y < r.Y + r.Height);
            if (row == null)
            {
                ClearSelection();
                e.Use();
                return;
            }

            var lane = new Rect(HeaderWidth, row.Y, contentWidth - HeaderWidth, row.Height);
            double mouseTime = XToTime(e.mousePosition.x);

            if (row.Track is TideMarkerTrack markerTrack)
            {
                TideMarker marker = markerTrack.Markers.LastOrDefault(m =>
                    Mathf.Abs(TimeToX(m.Time) - e.mousePosition.x) <= MarkerGrab);
                SelectTrack(markerTrack);
                _selectedMarker = marker;
                if (e.button == 1)
                {
                    ShowMarkerMenu(markerTrack, marker, mouseTime);
                }
                else if (marker != null)
                {
                    BeginEdit(dragControl, DragMode.MoveMarker, "Move Marker");
                    _dragGrab = mouseTime - marker.Time;
                }
                e.Use();
                return;
            }

            if (row.Track is not TideClipTrack clipTrack)
                return;
            TideClip clip = null;
            Rect clipRect = default;
            // Last drawn is on top
            for (int i = clipTrack.Clips.Count - 1; i >= 0; i--)
            {
                if (clipTrack.Clips[i] == null)
                    continue;
                Rect r = ClipRect(clipTrack.Clips[i], lane);
                if (r.Contains(e.mousePosition))
                {
                    clip = clipTrack.Clips[i];
                    clipRect = r;
                    break;
                }
            }
            SelectTrack(clipTrack);
            _selectedClip = clip;

            if (e.button == 1)
            {
                if (clip != null)
                    ShowClipMenu(clipTrack, clip);
                else
                    ShowLaneMenu(clipTrack, Snap(mouseTime, true));
                e.Use();
                return;
            }
            if (clip == null)
            {
                e.Use();
                return;
            }
            if (e.clickCount == 2 && clip is TideSubTimelineClip && BindingOf(clipTrack) is TidePlayer child)
            {
                SetPlayer(child);
                e.Use();
                return;
            }

            float grab = Mathf.Min(EdgeGrab, clipRect.width / 3f);
            if (e.mousePosition.x <= clipRect.x + grab)
            {
                BeginEdit(dragControl, DragMode.TrimStart, "Trim Clip");
                _dragFixedEnd = clip.End;
            }
            else if (e.mousePosition.x >= clipRect.xMax - grab)
            {
                BeginEdit(dragControl, DragMode.TrimEnd, "Trim Clip");
            }
            else
            {
                BeginEdit(dragControl, DragMode.MoveClip, "Move Clip");
                _dragGrab = mouseTime - clip.Start;
            }
            e.Use();
        }

        private void BeginEdit(int dragControl, DragMode mode, string label)
        {
            GUIUtility.hotControl = dragControl;
            _drag = mode;
            Undo.IncrementCurrentGroup();
            Undo.SetCurrentGroupName(label);
            _dragUndoGroup = Undo.GetCurrentGroup();
        }

        // Drags that started anywhere above, followed in window coordinates
        private void HandleDrag(int dragControl)
        {
            Event e = Event.current;
            if (GUIUtility.hotControl != dragControl || _drag == DragMode.None)
                return;

            switch (e.GetTypeForControl(dragControl))
            {
                case EventType.MouseDrag:
                    DragTo(e);
                    e.Use();
                    break;
                case EventType.MouseUp:
                    EndDrag();
                    GUIUtility.hotControl = 0;
                    e.Use();
                    break;
            }
        }

        private void DragTo(Event e)
        {
            double mouseTime = XToTime(e.mousePosition.x);
            bool snap = !e.shift;
            switch (_drag)
            {
                case DragMode.Scrub:
                    SetTime(Snap(mouseTime, snap));
                    break;
                case DragMode.Pan:
                    viewStart = Math.Max(0.0, viewStart - e.delta.x / pixelsPerSecond);
                    Repaint();
                    break;
                case DragMode.MoveClip when _selectedClip != null:
                    RecordDrag();
                    _selectedClip.Start = Snap(mouseTime - _dragGrab, snap);
                    OnTimelineChanged();
                    break;
                case DragMode.TrimStart when _selectedClip != null:
                    RecordDrag();
                    double start = Math.Min(Snap(mouseTime, snap), _dragFixedEnd - TideClip.MinDuration);
                    start = Math.Max(0.0, start);
                    _selectedClip.Start = start;
                    _selectedClip.Duration = _dragFixedEnd - start;
                    OnTimelineChanged();
                    break;
                case DragMode.TrimEnd when _selectedClip != null:
                    RecordDrag();
                    _selectedClip.Duration = Snap(mouseTime, snap) - _selectedClip.Start;
                    OnTimelineChanged();
                    break;
                case DragMode.MoveMarker when _selectedMarker != null:
                    RecordDrag();
                    _selectedMarker.Time = Snap(mouseTime - _dragGrab, snap);
                    OnTimelineChanged();
                    break;
            }
        }

        private void RecordDrag()
        {
            Undo.RecordObject(timeline, Undo.GetCurrentGroupName());
        }

        private void EndDrag()
        {
            if (_drag is DragMode.MoveClip or DragMode.TrimStart or DragMode.TrimEnd or DragMode.MoveMarker)
            {
                if (_selectedTrack is TideClipTrack clipTrack)
                    clipTrack.SortClips();
                else if (_selectedTrack is TideMarkerTrack markerTrack)
                    markerTrack.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
                EditorUtility.SetDirty(timeline);
                Undo.CollapseUndoOperations(_dragUndoGroup);
                OnTimelineChanged();
            }
            _drag = DragMode.None;
        }

        private void HandleDragAndDrop(Rect area)
        {
            Event e = Event.current;
            if (e.type is not (EventType.DragUpdated or EventType.DragPerform) || !area.Contains(e.mousePosition))
                return;
            GameObject dropped = DragAndDrop.objectReferences
                .Select(o => o is Component c ? c.gameObject : o as GameObject)
                .FirstOrDefault(g => g != null && !EditorUtility.IsPersistent(g));
            if (dropped == null)
                return;

            DragAndDrop.visualMode = PlayerPlaysTimeline ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
            if (e.type == EventType.DragPerform && PlayerPlaysTimeline)
            {
                DragAndDrop.AcceptDrag();
                var menu = new GenericMenu();
                TideTrackMenu.AddBindingItems(menu, dropped, player, (track, target) =>
                {
                    EditTimeline("Add Track", () => timeline.AddTrack(track));
                    SetBinding(track, target);
                    SelectTrack(track);
                });
                menu.ShowAsContext();
            }
            e.Use();
        }

        // ------------------------------------------------------------------ keys + menus

        private void HandleKeys()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown || GUIUtility.keyboardControl != 0)
                return;
            switch (e.keyCode)
            {
                case KeyCode.Space:
                    TogglePlayback();
                    break;
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    DeleteSelection();
                    break;
                case KeyCode.Home:
                    SetTime(0.0);
                    break;
                case KeyCode.End:
                    SetTime(Duration);
                    break;
                case KeyCode.LeftArrow:
                    SetTime(Math.Round(time / FrameDuration - 1.0) * FrameDuration);
                    break;
                case KeyCode.RightArrow:
                    SetTime(Math.Round(time / FrameDuration + 1.0) * FrameDuration);
                    break;
                case KeyCode.F:
                    FitView();
                    break;
                default:
                    return;
            }
            e.Use();
        }

        private void DeleteSelection()
        {
            if (_selectedClip != null && _selectedTrack is TideClipTrack clipTrack)
            {
                TideClip clip = _selectedClip;
                EditTimeline("Delete Clip", () => clipTrack.RemoveClip(clip));
                _selectedClip = null;
            }
            else if (_selectedMarker != null && _selectedTrack is TideMarkerTrack markerTrack)
            {
                TideMarker marker = _selectedMarker;
                EditTimeline("Delete Marker", () => markerTrack.Markers.Remove(marker));
                _selectedMarker = null;
            }
            else if (_selectedTrack != null)
            {
                DeleteTrack(_selectedTrack);
            }
        }

        private void ShowTrackMenu(TideTrack track)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(track.Muted ? "Unmute" : "Mute"), false,
                () => EditTimeline("Mute Track", () => track.Muted = !track.Muted));
            menu.AddItem(new GUIContent("Duplicate"), false, () => DuplicateTrack(track));
            menu.AddItem(new GUIContent("Move Up"), false, () => MoveTrack(track, -1));
            menu.AddItem(new GUIContent("Move Down"), false, () => MoveTrack(track, 1));
            Object binding = BindingOf(track);
            if (binding != null)
                menu.AddItem(new GUIContent("Select Bound Object"), false, () => Selection.activeObject = binding);
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete"), false, () => DeleteTrack(track));
            menu.ShowAsContext();
        }

        private void ShowLaneMenu(TideClipTrack track, double at)
        {
            var menu = new GenericMenu();
            foreach (Type type in TideTrackMenu.ClipTypes(track))
            {
                Type clipType = type;
                menu.AddItem(new GUIContent($"Add {TideStyles.KindName(type)} Clip"), false, () =>
                {
                    var clip = (TideClip)Activator.CreateInstance(clipType);
                    clip.Start = at;
                    clip.Duration = Math.Max(TideClip.MinDuration, Math.Min(1.0, Duration - at));
                    InitialiseClip(track, clip);
                    EditTimeline("Add Clip", () => track.AddClip(clip));
                    _selectedClip = clip;
                });
            }
            menu.ShowAsContext();
        }

        private void ShowClipMenu(TideClipTrack track, TideClip clip)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Duplicate"), false, () =>
            {
                var copy = (TideClip)Activator.CreateInstance(clip.GetType());
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(clip), copy);
                copy.Start = clip.End;
                EditTimeline("Duplicate Clip", () => track.AddClip(copy));
                _selectedClip = copy;
            });
            menu.AddItem(new GUIContent("Move To Playhead"), false, () => EditTimeline("Move Clip", () =>
            {
                clip.Start = time;
                track.SortClips();
            }));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Delete"), false, () =>
            {
                EditTimeline("Delete Clip", () => track.RemoveClip(clip));
                _selectedClip = null;
            });
            menu.ShowAsContext();
        }

        private void ShowMarkerMenu(TideMarkerTrack track, TideMarker marker, double at)
        {
            var menu = new GenericMenu();
            if (marker != null)
            {
                menu.AddItem(new GUIContent("Delete Marker"), false, () =>
                {
                    EditTimeline("Delete Marker", () => track.Markers.Remove(marker));
                    _selectedMarker = null;
                });
            }
            else
            {
                double snapped = Snap(at, true);
                menu.AddItem(new GUIContent("Add Marker"), false, () =>
                {
                    var added = new TideMarker { Time = snapped, Name = $"Marker {track.Markers.Count + 1}" };
                    EditTimeline("Add Marker", () =>
                    {
                        track.Markers.Add(added);
                        track.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
                    });
                    _selectedMarker = added;
                });
            }
            menu.ShowAsContext();
        }

        private void DuplicateTrack(TideTrack track)
        {
            var copy = (TideTrack)Activator.CreateInstance(track.GetType());
            EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(track), copy);
            copy.RegenerateId();
            copy.Name = TideStyles.TrackLabel(track) + " Copy";
            int index = IndexOf(track);
            EditTimeline("Duplicate Track", () => timeline.InsertTrack(index + 1, copy));
            Object binding = BindingOf(track);
            if (binding != null)
                SetBinding(copy, binding);
            SelectTrack(copy);
        }

        private void MoveTrack(TideTrack track, int offset)
        {
            int index = IndexOf(track);
            int target = Mathf.Clamp(index + offset, 0, timeline.Tracks.Count - 1);
            if (target == index)
                return;
            EditTimeline("Move Track", () =>
            {
                timeline.RemoveTrack(track);
                timeline.InsertTrack(target, track);
            });
        }

        private void DeleteTrack(TideTrack track)
        {
            EditTimeline("Delete Track", () => timeline.RemoveTrack(track));
            if (PlayerPlaysTimeline && player.GetBinding(track) != null)
                SetBinding(track, null);
            ClearSelection();
        }

        private int IndexOf(TideTrack track)
        {
            for (int i = 0; i < timeline.Tracks.Count; i++)
            {
                if (timeline.Tracks[i] == track)
                    return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ helpers

        private void SelectTrack(TideTrack track)
        {
            if (_selectedTrack != track)
            {
                _selectedClip = null;
                _selectedMarker = null;
            }
            _selectedTrack = track;
            GUIUtility.keyboardControl = 0;
            Repaint();
        }

        private void ClearSelection()
        {
            _selectedTrack = null;
            _selectedClip = null;
            _selectedMarker = null;
            Repaint();
        }

        private void FitView()
        {
            viewStart = 0.0;
            pixelsPerSecond = Mathf.Clamp((float)(_timelineWidth / (Duration * 1.05)), MinPixelsPerSecond, MaxPixelsPerSecond);
            Repaint();
        }

        private float TimeToX(double t)
        {
            return HeaderWidth + (float)((t - viewStart) * pixelsPerSecond);
        }

        private double XToTime(float x)
        {
            return Math.Max(0.0, viewStart + (x - HeaderWidth) / pixelsPerSecond);
        }

        private Rect ClipRect(TideClip clip, Rect lane)
        {
            float x0 = TimeToX(clip.Start);
            float x1 = TimeToX(clip.End);
            return new Rect(x0, lane.y + 3f, Mathf.Max(3f, x1 - x0), lane.height - 7f);
        }

        // Frames, and the playhead when near it; Shift turns snapping off
        private double Snap(double t, bool snap)
        {
            t = Math.Max(0.0, t);
            if (!snap)
                return t;
            if (_drag != DragMode.Scrub && Math.Abs(t - time) * pixelsPerSecond < SnapPixels)
                return time;
            return Math.Round(t / FrameDuration) * FrameDuration;
        }
    }
}
