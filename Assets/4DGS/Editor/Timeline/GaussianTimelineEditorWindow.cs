using System;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Editor
{
    /// <summary>
    /// Editor for <see cref="GaussianTimeline"/> assets, played through a
    /// <see cref="GaussianTimelinePlayer"/> in the open scene. Tracks are
    /// grouped by the GameObject they drive (click a group to select it); drop
    /// a scene object on the tracks to add a track bound to it. Drag clips to
    /// move them, their edges to trim them; right-click for more. Scrubbing or
    /// playing previews the timeline on the scene without Play mode: the
    /// player binds (remembering its targets' values) and unbinds before the
    /// scene is saved, scripts reload or Play mode starts. In Play mode the
    /// window follows and drives the playing player.
    /// </summary>
    public partial class GaussianTimelineEditorWindow : EditorWindow
    {
        private const string Title = "Gaussian Timeline";

        [SerializeField] private GaussianTimelinePlayer player;
        [SerializeField] private GaussianTimeline timeline;
        [SerializeField] private double time;
        [SerializeField] private double viewStart;
        [SerializeField] private float pixelsPerSecond = 80f;
        [SerializeField] private bool previewEnabled = true;

        private bool _playing;
        private double _lastTick;

        [MenuItem("Window/Gaussian Splatting/Timeline")]
        public static GaussianTimelineEditorWindow Open()
        {
            var window = GetWindow<GaussianTimelineEditorWindow>(Title);
            window.Show();
            return window;
        }

        public static void Open(GaussianTimelinePlayer player)
        {
            Open().SetPlayer(player);
        }

        public static void Open(GaussianTimeline timeline)
        {
            Open().SetTimeline(timeline);
        }

        [OnOpenAsset]
        private static bool OpenTimelineAsset(EntityId entityId, int line)
        {
            if (EditorUtility.EntityIdToObject(entityId) is not GaussianTimeline asset)
                return false;
            Open(asset);
            return true;
        }

        private void OnEnable()
        {
            wantsMouseMove = true;
            Undo.undoRedoPerformed += OnUndoRedo;
            EditorSceneManager.sceneSaving += OnSceneSaving;
            EditorSceneManager.sceneSaved += OnSceneSaved;
            EditorSceneManager.sceneClosing += OnSceneClosing;
            AssemblyReloadEvents.beforeAssemblyReload += EndPreview;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.update += OnEditorUpdate;
            EditorSceneManager.sceneOpened += OnSceneOpened;
            EditorApplication.hierarchyChanged += ResolvePlayer;
            ResolvePlayer();
            ApplyPreview();
        }

        private void OnDisable()
        {
            StopPlayback();
            EndPreview();
            Undo.undoRedoPerformed -= OnUndoRedo;
            EditorSceneManager.sceneSaving -= OnSceneSaving;
            EditorSceneManager.sceneSaved -= OnSceneSaved;
            EditorSceneManager.sceneClosing -= OnSceneClosing;
            AssemblyReloadEvents.beforeAssemblyReload -= EndPreview;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.update -= OnEditorUpdate;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorApplication.hierarchyChanged -= ResolvePlayer;
        }

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject != null &&
                Selection.activeGameObject.TryGetComponent(out GaussianTimelinePlayer selected) && selected != player)
            {
                SetPlayer(selected);
            }
            else if (Selection.activeObject is GaussianTimeline asset && asset != timeline)
            {
                SetTimeline(asset);
            }
        }

        // ------------------------------------------------------------------ what is edited

        private void SetPlayer(GaussianTimelinePlayer newPlayer)
        {
            if (newPlayer == player && (newPlayer == null || newPlayer.Timeline == timeline))
                return;
            StopPlayback();
            EndPreview();
            player = newPlayer;
            timeline = newPlayer != null ? newPlayer.Timeline : timeline;
            ClearSelection();
            time = player != null && EditorApplication.isPlaying ? player.CurrentTime : Math.Min(time, Duration);
            ApplyPreview();
            Repaint();
        }

        // An asset on its own: preview it through a player in the open scenes that plays it, if any
        private void SetTimeline(GaussianTimeline asset)
        {
            if (asset == timeline)
                return;
            StopPlayback();
            EndPreview();
            timeline = asset;
            player = FindPlayer(asset);
            ClearSelection();
            time = Math.Min(time, Duration);
            ApplyPreview();
            Repaint();
        }

        // The player reference dies with its scene (reopened, reverted, or
        // played with scene reload); pick up a player of the same timeline again
        private void ResolvePlayer()
        {
            if (player != null || timeline == null)
                return;
            player = FindPlayer(timeline);
            if (player == null)
                return;
            if (EditorApplication.isPlaying)
                time = player.CurrentTime;
            ApplyPreview();
            Repaint();
        }

        private static GaussianTimelinePlayer FindPlayer(GaussianTimeline asset)
        {
            if (asset == null)
                return null;
            foreach (GaussianTimelinePlayer candidate in FindObjectsByType<GaussianTimelinePlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (candidate.Timeline == asset)
                    return candidate;
            }
            return null;
        }

        private double Duration => timeline != null ? timeline.Duration : 10.0;

        private double FrameDuration => timeline != null ? 1.0 / timeline.FrameRate : 1.0 / 30.0;

        // Bindings and preview need the player to play this very timeline
        private bool PlayerPlaysTimeline => player != null && timeline != null && player.Timeline == timeline;

        // ------------------------------------------------------------------ editing

        /// <summary>Records the timeline for undo, applies <paramref name="edit"/> and refreshes the preview.</summary>
        private void EditTimeline(string label, Action edit)
        {
            if (timeline == null)
                return;
            Undo.RecordObject(timeline, label);
            edit();
            EditorUtility.SetDirty(timeline);
            OnTimelineChanged();
        }

        private void SetBinding(GaussianTimelineTrack track, Object target)
        {
            if (!PlayerPlaysTimeline)
                return;
            Undo.RecordObject(player, "Bind Timeline Track");
            player.SetBinding(track, target);
            PrefabUtility.RecordPrefabInstancePropertyModifications(player);
            EditorUtility.SetDirty(player);
            OnTimelineChanged();
        }

        private void OnTimelineChanged()
        {
            if (PlayerPlaysTimeline && player.IsBound)
                player.Rebind();
            ApplyPreview();
            Repaint();
        }

        private void OnUndoRedo()
        {
            if (_selectedTrack != null && timeline != null && timeline.FindTrack(_selectedTrack.Id) == null)
                ClearSelection();
            OnTimelineChanged();
        }

        /// <summary>
        /// Reads scene values the preview would otherwise hide: unbinds so the
        /// targets show their own values, runs <paramref name="read"/>, previews again.
        /// </summary>
        private void ReadSceneValues(Action read)
        {
            EndPreview();
            read();
            ApplyPreview();
        }

        // ------------------------------------------------------------------ preview + playback

        private void SetTime(double newTime)
        {
            time = Math.Clamp(newTime, 0.0, Duration);
            if (EditorApplication.isPlaying)
            {
                if (PlayerPlaysTimeline)
                    player.Seek(time);
            }
            else
            {
                ApplyPreview();
            }
            Repaint();
        }

        private void ApplyPreview()
        {
            if (!PlayerPlaysTimeline || EditorApplication.isPlayingOrWillChangePlaymode)
                return;
            if (!previewEnabled)
            {
                EndPreview();
                return;
            }
            player.Seek(time);
            RefreshViews();
        }

        private void EndPreview()
        {
            if (player == null || EditorApplication.isPlaying || !player.IsBound)
                return;
            player.Unbind();
            RefreshViews();
        }

        private void TogglePlayback()
        {
            if (EditorApplication.isPlaying)
            {
                if (!PlayerPlaysTimeline)
                    return;
                if (player.IsPlaying)
                    player.Pause();
                else
                    player.Play();
                return;
            }
            if (_playing)
                StopPlayback();
            else
                StartPlayback();
        }

        private void StartPlayback()
        {
            if (timeline == null)
                return;
            if (time >= Duration)
                time = 0.0;
            _playing = true;
            _lastTick = EditorApplication.timeSinceStartup;
        }

        private void StopPlayback()
        {
            _playing = false;
        }

        private bool IsPlaying => EditorApplication.isPlaying ? PlayerPlaysTimeline && player.IsPlaying : _playing;

        private void OnEditorUpdate()
        {
            if (EditorApplication.isPlaying)
            {
                // Follow the player
                if (PlayerPlaysTimeline && Math.Abs(player.CurrentTime - time) > 1e-6)
                {
                    time = player.CurrentTime;
                    Repaint();
                }
                return;
            }
            if (!_playing)
                return;

            double now = EditorApplication.timeSinceStartup;
            double next = time + (now - _lastTick) * (player != null ? player.PlaybackSpeed : 1f);
            _lastTick = now;
            if (next >= Duration)
            {
                if (player != null && player.WrapMode == GaussianTimelineWrapMode.Loop)
                {
                    next %= Duration;
                }
                else
                {
                    next = Duration;
                    StopPlayback();
                }
            }
            SetTime(next);
        }

        private void OnSceneSaving(Scene scene, string path)
        {
            EndPreview();
        }

        private void OnSceneSaved(Scene scene)
        {
            ApplyPreview();
        }

        private void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            ResolvePlayer();
        }

        private void OnSceneClosing(Scene scene, bool removingScene)
        {
            if (player != null && player.gameObject.scene == scene)
            {
                StopPlayback();
                EndPreview();
            }
        }

        private void OnPlayModeChanged(PlayModeStateChange change)
        {
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    StopPlayback();
                    EndPreview();
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    ResolvePlayer();
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    ResolvePlayer();
                    ApplyPreview();
                    Repaint();
                    break;
            }
        }

        // Lets [ExecuteAlways] effects update and redraws the Scene and Game views
        private static void RefreshViews()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }
    }
}
