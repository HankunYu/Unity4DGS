using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Plays a <see cref="GaussianTimeline"/> in the scene. Each track is
    /// bound to a scene object through this player's binding table; binding
    /// remembers the targets' own values and unbinding (Stop, disabling the
    /// player) puts them back. Every evaluation depends on the time alone, so
    /// seeking anywhere shows the same frame playback would; that includes the
    /// animated phases of the modifiers the tracks drive, which come from a
    /// table built at binding (see <see cref="GaussianTimelinePhaseTable"/>).
    /// A player that drives the scene clock (<see cref="GaussianSceneClock"/>)
    /// also sets the time of the effects no track drives, modifier phases and
    /// style animation, so a frame depends on the timeline alone.
    /// Runs before other scripts so effects see this frame's values.
    /// </summary>
    [AddComponentMenu("Gaussian Splatting/Timeline/Timeline Player")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public class GaussianTimelinePlayer : MonoBehaviour
    {
        // Audio time moves in mixer-buffer steps: follow it smoothly and only
        // jump when it really drifts off
        private const double AudioResyncThreshold = 0.1;
        private const double AudioDriftCorrection = 0.1;
        private const int MaxNesting = 8;

        [SerializeField] private GaussianTimeline timeline;

        [SerializeField] private GaussianTimelineUpdateMode updateMode = GaussianTimelineUpdateMode.GameTime;

        [Tooltip("Audio update mode: the source whose playback the time follows. It plays, pauses and seeks with the timeline")]
        [SerializeField] private AudioSource audioClock;

        [SerializeField] private bool playOnStart = true;

        [SerializeField] private GaussianTimelineWrapMode wrapMode = GaussianTimelineWrapMode.Hold;

        [Min(0f)]
        [SerializeField] private float playbackSpeed = 1f;

        [Tooltip("While bound, set the time every animation modifier and style follows, not only those the tracks drive, so each frame depends on the timeline alone (repeatable captures). Leave it on for the scene's main timeline only")]
        [SerializeField] private bool drivesSceneClock = true;

        [SerializeField] private List<GaussianTimelineBinding> bindings = new();

        private readonly List<GaussianTimelineTrackInstance> _instances = new();
        private readonly List<GaussianTimelineTrackInstance> _phaseInstances = new();
        private GaussianTimelinePhaseTable _phaseTable;
        private bool _bound;
        private bool _playing;
        private double _time;
        // The player whose sub-timeline track drives this one, if any
        private GaussianTimelinePlayer _driver;

#if UNITY_EDITOR
        // Players bound in edit mode (a timeline window preview, a script),
        // which get no OnDisable: they are unbound before a domain reload or
        // quitting, so their targets are restored and their playable graphs
        // destroyed
        private static readonly HashSet<GaussianTimelinePlayer> s_editModeBound = new();

        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorShutdown()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += UnbindEditModePlayers;
            UnityEditor.EditorApplication.quitting += UnbindEditModePlayers;
        }

        private static void UnbindEditModePlayers()
        {
            foreach (GaussianTimelinePlayer player in new List<GaussianTimelinePlayer>(s_editModeBound))
            {
                if (player != null)
                    player.Unbind();
            }
            s_editModeBound.Clear();
        }
#endif

        public event Action OnLooped;

        public event Action OnFinished;

        /// <summary>Raised with the marker's name when playback passes a marker.</summary>
        public event Action<string> OnMarker;

        public GaussianTimeline Timeline
        {
            get => timeline;
            set
            {
                if (timeline == value)
                    return;
                bool wasBound = _bound;
                Unbind();
                timeline = value;
                _time = Math.Min(_time, Duration);
                if (wasBound)
                    Seek(_time);
            }
        }

        public GaussianTimelineUpdateMode UpdateMode
        {
            get => updateMode;
            set => updateMode = value;
        }

        public AudioSource AudioClock
        {
            get => audioClock;
            set => audioClock = value;
        }

        public bool PlayOnStart
        {
            get => playOnStart;
            set => playOnStart = value;
        }

        public GaussianTimelineWrapMode WrapMode
        {
            get => wrapMode;
            set => wrapMode = value;
        }

        public float PlaybackSpeed
        {
            get => playbackSpeed;
            set => playbackSpeed = Mathf.Max(0f, value);
        }

        public bool DrivesSceneClock
        {
            get => drivesSceneClock;
            set
            {
                if (drivesSceneClock == value)
                    return;
                drivesSceneClock = value;
                Rebind();
            }
        }

        public double CurrentTime => _time;

        public double Duration => timeline != null ? timeline.Duration : 0.0;

        public bool IsPlaying => _playing;

        public bool IsBound => _bound;

        /// <summary>Whether another player's sub-timeline track drives this one.</summary>
        public bool IsDriven => _driver != null;

        public IReadOnlyList<GaussianTimelineBinding> Bindings => bindings;

        private void Start()
        {
            if (playOnStart && _driver == null)
                Play();
        }

        private void OnDisable()
        {
            _playing = false;
            if (_driver == null)
                StopAudioClock();
            Unbind();
        }

        private void Update()
        {
            if (!_playing || _driver != null || timeline == null)
                return;

            double previous = _time;
            _time = NextTime();
            double duration = timeline.Duration;
            bool wrapped = false;
            bool finished = false;
            if (_time >= duration)
            {
                if (wrapMode == GaussianTimelineWrapMode.Loop)
                {
                    _time %= duration;
                    wrapped = true;
                    SeekAudioClock();
                }
                else
                {
                    _time = duration;
                    finished = true;
                }
            }

            EvaluateAt(_time, previous, true, wrapped);
            if (wrapped)
                OnLooped?.Invoke();
            if (finished)
            {
                _playing = false;
                StopAudioClock();
                if (wrapMode == GaussianTimelineWrapMode.None)
                    Unbind();
                OnFinished?.Invoke();
            }
        }

        /// <summary>Plays on from the current time (from the start once playback has finished).</summary>
        public void Play()
        {
            if (timeline == null || _driver != null)
                return;
            if (_time >= Duration)
                _time = 0.0;
            Bind();
            _playing = true;
            PlayAudioClock();
            EvaluateAt(_time, _time, false, false);
        }

        public void Pause()
        {
            if (!_playing)
                return;
            _playing = false;
            if (audioClock != null && updateMode == GaussianTimelineUpdateMode.Audio)
                audioClock.Pause();
            if (_bound)
                EvaluateAt(_time, _time, false, false);
        }

        /// <summary>Stops, rewinds and puts every target back as it was.</summary>
        public void Stop()
        {
            _playing = false;
            if (_driver == null)
                StopAudioClock();
            _time = 0.0;
            Unbind();
        }

        /// <summary>Jumps to <paramref name="time"/> and shows it, binding first if needed.</summary>
        public void Seek(double time)
        {
            if (timeline == null || _driver != null)
                return;
            _time = Math.Clamp(time, 0.0, Duration);
            SeekAudioClock();
            Bind();
            EvaluateAt(_time, _time, false, false);
        }

        public Object GetBinding(GaussianTimelineTrack track)
        {
            if (track == null)
                return null;
            int index = FindBinding(track.Id);
            return index >= 0 ? bindings[index].Target : null;
        }

        /// <summary>Binds <paramref name="track"/> to <paramref name="target"/> (null unbinds it).</summary>
        public void SetBinding(GaussianTimelineTrack track, Object target)
        {
            if (track == null)
                throw new ArgumentNullException(nameof(track));
            int index = FindBinding(track.Id);
            if (target == null)
            {
                if (index >= 0)
                    bindings.RemoveAt(index);
            }
            else if (index >= 0)
            {
                bindings[index] = new GaussianTimelineBinding(track.Id, target);
            }
            else
            {
                bindings.Add(new GaussianTimelineBinding(track.Id, target));
            }
            Rebind();
        }

        /// <summary>Drops bindings of tracks the timeline no longer has.</summary>
        public void PruneBindings()
        {
            bindings.RemoveAll(b => b.Target == null || timeline == null || timeline.FindTrack(b.TrackId) == null);
        }

        /// <summary>
        /// Creates the track instances, which remember their targets' values.
        /// Playing and seeking bind on their own; editors bind to preview.
        /// </summary>
        public void Bind()
        {
            if (_bound || timeline == null)
                return;
            _bound = true;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                s_editModeBound.Add(this);
#endif
            foreach (GaussianTimelineTrack track in timeline.Tracks)
            {
                if (track == null || track.Muted)
                    continue;
                Object target = GetBinding(track);
                if (target == null ? !track.BindingOptional : !track.CanBind(target))
                    continue;
                GaussianTimelineTrackInstance instance = track.CreateInstance(this, target);
                if (instance != null)
                    _instances.Add(instance);
            }
            BuildPhaseTable();
        }

        /// <summary>Puts every target back as it was before binding.</summary>
        public void Unbind()
        {
            if (!_bound)
                return;
            _bound = false;
#if UNITY_EDITOR
            s_editModeBound.Remove(this);
#endif
            // Last bound first, so a target driven by several tracks ends on
            // the value the first one found
            for (int i = _instances.Count - 1; i >= 0; i--)
                _instances[i].Restore();
            _instances.Clear();
            _phaseInstances.Clear();
            _phaseTable?.Release();
            _phaseTable = null;
            GaussianSceneClock.Release(this);
        }

        /// <summary>Recreates the track instances after the timeline or a binding changed.</summary>
        public void Rebind()
        {
            if (!_bound)
                return;
            Unbind();
            Bind();
            EvaluateAt(_time, _time, false, false);
        }

        // ------------------------------------------------------------------ sub-timelines

        internal bool BeginDriven(GaussianTimelinePlayer driver)
        {
            if (driver == null || driver == this || _driver != null)
                return false;
            int depth = 0;
            for (GaussianTimelinePlayer p = driver; p != null; p = p._driver)
            {
                if (p == this || ++depth > MaxNesting)
                    return false;
            }
            _driver = driver;
            _playing = false;
            StopAudioClock();
            return true;
        }

        internal void EvaluateDriven(double time, double previousTime, bool continuous)
        {
            Bind();
            double duration = Duration;
            _time = Math.Clamp(time, 0.0, duration);
            EvaluateAt(_time, Math.Clamp(previousTime, 0.0, duration), continuous, false);
        }

        // Out of the driving clips: targets go back to their own values
        internal void ReleaseDriven()
        {
            Unbind();
        }

        internal void EndDriven(GaussianTimelinePlayer driver)
        {
            if (_driver != driver)
                return;
            Unbind();
            _driver = null;
        }

        internal void RaiseMarker(string marker)
        {
            OnMarker?.Invoke(marker);
        }

        // ------------------------------------------------------------------ internals

        // Tabulates the phases of the modifiers the tracks drive, and of
        // every other modifier when this player drives the scene clock (their
        // own rates hold throughout). Steps only the tracks that drive
        // modifiers; the evaluation after binding puts every target back on
        // the current time.
        private void BuildPhaseTable()
        {
            var modifiers = new List<GaussianAnimModifier>();
            foreach (GaussianTimelineTrackInstance instance in _instances)
            {
                int before = modifiers.Count;
                instance.CollectPhaseTargets(modifiers);
                if (modifiers.Count > before)
                    _phaseInstances.Add(instance);
            }
            if (DrivesClock)
                modifiers.AddRange(FindObjectsByType<GaussianAnimModifier>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            modifiers = modifiers.Distinct().Where(m => m != null && m.PhaseCount > 0).ToList();
            if (modifiers.Count == 0)
                return;

            double duration = Duration;
            _phaseTable = new GaussianTimelinePhaseTable(this, modifiers, duration, timeline.FrameRate, t =>
            {
                var evaluation = new GaussianTimelineEvaluation(t, t, duration, false, false);
                foreach (GaussianTimelineTrackInstance instance in _phaseInstances)
                    instance.Evaluate(evaluation);
            });
            _phaseTable.Attach();
        }

        // A sub-timeline follows its parent's time, which already drives the clock
        private bool DrivesClock => drivesSceneClock && _driver == null;

        private void EvaluateAt(double time, double previousTime, bool continuous, bool wrapped)
        {
            if (DrivesClock)
                GaussianSceneClock.Drive(this, time);
            var evaluation = new GaussianTimelineEvaluation(time, previousTime, Duration, continuous, wrapped);
            for (int i = 0; i < _instances.Count; i++)
                _instances[i].Evaluate(evaluation);
        }

        private double NextTime()
        {
            switch (updateMode)
            {
                case GaussianTimelineUpdateMode.GameTime:
                    return _time + UnityEngine.Time.deltaTime * playbackSpeed;
                case GaussianTimelineUpdateMode.UnscaledGameTime:
                    return _time + UnityEngine.Time.unscaledDeltaTime * playbackSpeed;
                case GaussianTimelineUpdateMode.Audio:
                    double predicted = _time + UnityEngine.Time.unscaledDeltaTime * playbackSpeed;
                    if (audioClock == null || audioClock.clip == null || !audioClock.isPlaying)
                        return predicted;
                    double audioTime = audioClock.timeSamples / (double)audioClock.clip.frequency;
                    double drift = audioTime - predicted;
                    return Math.Abs(drift) > AudioResyncThreshold ? audioTime : predicted + drift * AudioDriftCorrection;
                default:
                    return _time;
            }
        }

        private void PlayAudioClock()
        {
            if (updateMode != GaussianTimelineUpdateMode.Audio || audioClock == null || audioClock.clip == null)
                return;
            audioClock.pitch = playbackSpeed;
            if (_time >= audioClock.clip.length)
                return;
            audioClock.time = (float)_time;
            audioClock.Play();
        }

        private void SeekAudioClock()
        {
            if (updateMode != GaussianTimelineUpdateMode.Audio || audioClock == null || audioClock.clip == null)
                return;
            if (_time < audioClock.clip.length)
            {
                audioClock.time = (float)_time;
                if (_playing && !audioClock.isPlaying)
                    audioClock.Play();
            }
            else
            {
                audioClock.Stop();
            }
        }

        private void StopAudioClock()
        {
            if (updateMode == GaussianTimelineUpdateMode.Audio && audioClock != null)
                audioClock.Stop();
        }

        private int FindBinding(string trackId)
        {
            for (int i = 0; i < bindings.Count; i++)
            {
                if (bindings[i].TrackId == trackId)
                    return i;
            }
            return -1;
        }
    }
}
