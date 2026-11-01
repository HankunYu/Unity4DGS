using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tide
{
    /// <summary>
    /// Plays a <see cref="TideTimeline"/> in the scene. Each track is
    /// bound to a scene object through this player's binding table; binding
    /// remembers the targets' own values and unbinding (Stop, disabling the
    /// player) puts them back. Every evaluation depends on the time alone, so
    /// seeking anywhere shows the same frame playback would; that includes the
    /// phases of the <see cref="ITidePhaseTarget"/>s the tracks drive, which
    /// come from a table built at binding (see <see cref="TidePhaseTable"/>).
    /// A player that drives the scene clock (<see cref="TideClock"/>) also
    /// sets the time of everything no track drives: every phase target in
    /// the scene and whatever reads the clock, so a frame depends on the
    /// timeline alone.
    /// Runs before other scripts so effects see this frame's values.
    /// </summary>
    [AddComponentMenu("Tide/Tide Player")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-200)]
    public class TidePlayer : MonoBehaviour
    {
        // Audio time moves in mixer-buffer steps: follow it smoothly and only
        // jump when it really drifts off
        private const double AudioResyncThreshold = 0.1;
        private const double AudioDriftCorrection = 0.1;
        private const int MaxNesting = 8;

        [SerializeField] private TideTimeline timeline;

        [SerializeField] private TideUpdateMode updateMode = TideUpdateMode.GameTime;

        [Tooltip("Audio update mode: the source whose playback the time follows. It plays, pauses and seeks with the timeline")]
        [SerializeField] private AudioSource audioClock;

        [SerializeField] private bool playOnStart = true;

        [SerializeField] private TideWrapMode wrapMode = TideWrapMode.Hold;

        [Min(0f)]
        [SerializeField] private float playbackSpeed = 1f;

        [Tooltip("While bound, set the time every phase target and TideClock reader follows, not only what the tracks drive, so each frame depends on the timeline alone (repeatable captures). Leave it on for the scene's main timeline only")]
        [SerializeField] private bool drivesSceneClock = true;

        [SerializeField] private List<TideBinding> bindings = new();

        private readonly List<TideTrackInstance> _instances = new();
        private readonly List<TideTrackInstance> _phaseInstances = new();
        private TidePhaseTable _phaseTable;
        private bool _bound;
        private bool _playing;
        private double _time;
        // The player whose sub-timeline track drives this one, if any
        private TidePlayer _driver;

#if UNITY_EDITOR
        // Players bound in edit mode (a timeline window preview, a script),
        // which get no OnDisable: they are unbound before a domain reload or
        // quitting, so their targets are restored and their playable graphs
        // destroyed
        private static readonly HashSet<TidePlayer> s_editModeBound = new();

        [UnityEditor.InitializeOnLoadMethod]
        private static void HookEditorShutdown()
        {
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += UnbindEditModePlayers;
            UnityEditor.EditorApplication.quitting += UnbindEditModePlayers;
        }

        private static void UnbindEditModePlayers()
        {
            foreach (TidePlayer player in new List<TidePlayer>(s_editModeBound))
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

        public TideTimeline Timeline
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

        public TideUpdateMode UpdateMode
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

        public TideWrapMode WrapMode
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

        public IReadOnlyList<TideBinding> Bindings => bindings;

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
                if (wrapMode == TideWrapMode.Loop)
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
                if (wrapMode == TideWrapMode.None)
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
            if (audioClock != null && updateMode == TideUpdateMode.Audio)
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

        public Object GetBinding(TideTrack track)
        {
            if (track == null)
                return null;
            int index = FindBinding(track.Id);
            return index >= 0 ? bindings[index].Target : null;
        }

        /// <summary>Binds <paramref name="track"/> to <paramref name="target"/> (null unbinds it).</summary>
        public void SetBinding(TideTrack track, Object target)
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
                bindings[index] = new TideBinding(track.Id, target);
            }
            else
            {
                bindings.Add(new TideBinding(track.Id, target));
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
            foreach (TideTrack track in timeline.Tracks)
            {
                if (track == null || track.Muted)
                    continue;
                Object target = GetBinding(track);
                if (target == null ? !track.BindingOptional : !track.CanBind(target))
                    continue;
                TideTrackInstance instance = track.CreateInstance(this, target);
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
            TideClock.Release(this);
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

        internal bool BeginDriven(TidePlayer driver)
        {
            if (driver == null || driver == this || _driver != null)
                return false;
            int depth = 0;
            for (TidePlayer p = driver; p != null; p = p._driver)
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

        internal void EndDriven(TidePlayer driver)
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

        // Tabulates the phases of the targets the tracks drive, and of every
        // other phase target in the scene when this player drives the clock
        // (their own rates hold throughout). Steps only the tracks that drive
        // targets; the evaluation after binding puts every target back on
        // the current time.
        private void BuildPhaseTable()
        {
            var targets = new List<ITidePhaseTarget>();
            foreach (TideTrackInstance instance in _instances)
            {
                int before = targets.Count;
                instance.CollectPhaseTargets(targets);
                if (targets.Count > before)
                    _phaseInstances.Add(instance);
            }
            if (DrivesClock)
            {
                foreach (MonoBehaviour behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (behaviour is ITidePhaseTarget target)
                        targets.Add(target);
                }
            }
            targets = targets.Distinct().Where(t => t is not Object o || o != null).Where(t => t != null && t.PhaseCount > 0).ToList();
            if (targets.Count == 0)
                return;

            double duration = Duration;
            _phaseTable = new TidePhaseTable(this, targets, duration, timeline.FrameRate, t =>
            {
                var evaluation = new TideEvaluation(t, t, duration, false, false);
                foreach (TideTrackInstance instance in _phaseInstances)
                    instance.Evaluate(evaluation);
            });
            _phaseTable.Attach();
        }

        // A sub-timeline follows its parent's time, which already drives the clock
        private bool DrivesClock => drivesSceneClock && _driver == null;

        private void EvaluateAt(double time, double previousTime, bool continuous, bool wrapped)
        {
            if (DrivesClock)
                TideClock.Drive(this, time);
            var evaluation = new TideEvaluation(time, previousTime, Duration, continuous, wrapped);
            for (int i = 0; i < _instances.Count; i++)
                _instances[i].Evaluate(evaluation);
        }

        private double NextTime()
        {
            switch (updateMode)
            {
                case TideUpdateMode.GameTime:
                    return _time + UnityEngine.Time.deltaTime * playbackSpeed;
                case TideUpdateMode.UnscaledGameTime:
                    return _time + UnityEngine.Time.unscaledDeltaTime * playbackSpeed;
                case TideUpdateMode.Audio:
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
            if (updateMode != TideUpdateMode.Audio || audioClock == null || audioClock.clip == null)
                return;
            audioClock.pitch = playbackSpeed;
            if (_time >= audioClock.clip.length)
                return;
            audioClock.time = (float)_time;
            audioClock.Play();
        }

        private void SeekAudioClock()
        {
            if (updateMode != TideUpdateMode.Audio || audioClock == null || audioClock.clip == null)
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
            if (updateMode == TideUpdateMode.Audio && audioClock != null)
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
