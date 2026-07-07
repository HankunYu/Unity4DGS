using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// State machine that controls GaussianAnimModifier parameters on a GaussianAnimVolume.
    /// Attach to the same GameObject as a GaussianAnimVolume.
    /// Define states with modifier parameter snapshots, then trigger transitions via code or auto-rules.
    /// </summary>
    [RequireComponent(typeof(GaussianAnimVolume))]
    public class GaussianModifierStateMachine : MonoBehaviour
    {
        private static readonly StateTransition DefaultTransition = new()
        {
            duration = 0.3f,
            easeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f)
        };

        [Header("States")]
        [SerializeField] private List<ModifierState> _states = new();
        [SerializeField] private string _defaultState;

        [Header("Events")]
        [SerializeField] private UnityEvent<string> _onStateChanged;

        // Runtime state
        private string _currentStateName;
        private ModifierState _currentState;
        private bool _isTransitioning;
        private float _transitionElapsed;
        private float _transitionDuration;
        private AnimationCurve _transitionCurve;
        private ModifierState _transitionTarget;
        private List<ModifierParam> _transitionFromSnapshot;

        // Auto-transition timer
        private float _autoDelayTimer;
        private StateTransition _pendingAutoTransition;

        public string CurrentState => _currentStateName;
        public bool IsTransitioning => _isTransitioning;
        public List<ModifierState> States => _states;

        private void OnEnable()
        {
            if (!string.IsNullOrEmpty(_defaultState))
            {
                SetStateImmediate(_defaultState);
            }
        }

        private void Update()
        {
            if (_isTransitioning)
            {
                UpdateTransition();
            }
            else if (_pendingAutoTransition != null)
            {
                UpdateAutoDelay();
            }
        }

        /// <summary>
        /// Trigger a smooth transition to the named state.
        /// Uses transition rules defined on the current state, or defaults if none found.
        /// </summary>
        public void SetState(string stateName)
        {
            if (_currentStateName == stateName && !_isTransitioning) return;
            // Already heading there — don't restart. A restart re-snapshots and
            // resets elapsed time, so per-frame SetState calls would never
            // complete with an ease-in curve (and would re-fire onExit).
            if (_isTransitioning && _transitionTarget != null && _transitionTarget.name == stateName) return;

            ModifierState target = FindState(stateName);
            if (target == null)
            {
                Debug.LogWarning($"GaussianModifierStateMachine: State '{stateName}' not found.", this);
                return;
            }

            StateTransition transition = _currentState?.FindTransitionTo(stateName) ?? DefaultTransition;
            BeginTransition(target, transition);
        }

        /// <summary>
        /// Immediately jump to the named state with no interpolation.
        /// </summary>
        public void SetStateImmediate(string stateName)
        {
            ModifierState target = FindState(stateName);
            if (target == null)
            {
                Debug.LogWarning($"GaussianModifierStateMachine: State '{stateName}' not found.", this);
                return;
            }

            CancelTransition();
            ApplyStateImmediate(target);
        }

        /// <summary>
        /// Capture current modifier values into the state at the given index.
        /// Works in both Editor and Play mode.
        /// </summary>
        public void CaptureStateParams(int stateIndex)
        {
            if (stateIndex < 0 || stateIndex >= _states.Count)
            {
                Debug.LogWarning($"GaussianModifierStateMachine: State index {stateIndex} out of range.", this);
                return;
            }

            ModifierState state = _states[stateIndex];
            GaussianAnimModifier[] modifiers = GetComponents<GaussianAnimModifier>();
            state.modifierParams.Clear();

            foreach (GaussianAnimModifier modifier in modifiers)
            {
                var param = new ModifierParam { target = modifier };
                param.CaptureFromTarget();
                state.modifierParams.Add(param);
            }

            Debug.Log($"Captured {modifiers.Length} modifier(s) into state '{state.name}'.", this);
        }

        /// <summary>
        /// Apply the state's stored parameter values to the modifier components.
        /// Works in both Editor and Play mode.
        /// </summary>
        public void ApplyStateParams(int stateIndex)
        {
            if (stateIndex < 0 || stateIndex >= _states.Count)
            {
                Debug.LogWarning($"GaussianModifierStateMachine: State index {stateIndex} out of range.", this);
                return;
            }

            ModifierState state = _states[stateIndex];
            foreach (ModifierParam param in state.modifierParams)
            {
                if (param.target == null) continue;
                param.target.enabled = param.enabled;
                param.ApplyToTarget();
            }

            Debug.Log($"Applied state '{state.name}' to modifiers.", this);
        }

        /// <summary>
        /// Auto-populate all empty states with current modifier snapshots.
        /// Works in both Editor and Play mode.
        /// </summary>
        public void AutoPopulateModifiers()
        {
            GaussianAnimModifier[] modifiers = GetComponents<GaussianAnimModifier>();
            foreach (ModifierState state in _states)
            {
                if (state.modifierParams.Count > 0) continue;

                foreach (GaussianAnimModifier modifier in modifiers)
                {
                    var param = new ModifierParam { target = modifier };
                    param.CaptureFromTarget();
                    state.modifierParams.Add(param);
                }
            }

            Debug.Log($"Auto-populated {_states.Count} state(s) with {modifiers.Length} modifier(s).", this);
        }

        private void BeginTransition(ModifierState target, StateTransition transition)
        {
            bool wasTransitioning = _isTransitioning;
            CancelTransition();

            // Snapshot current params as "from"
            _transitionFromSnapshot = SnapshotCurrentParams(target);

            // Fire exit event on current state. When interrupting an in-flight
            // transition, the current state's onExit already fired when that
            // transition began — don't fire it a second time.
            if (!wasTransitioning)
                _currentState?.onExit?.Invoke();

            // Enable modifiers that the target state needs
            foreach (ModifierParam param in target.modifierParams)
            {
                if (param.target != null && param.enabled)
                    param.target.enabled = true;
            }

            _transitionTarget = target;
            _transitionDuration = Mathf.Max(transition.duration, 0.001f);
            _transitionCurve = transition.easeCurve;
            _transitionElapsed = 0f;
            _isTransitioning = true;
            _pendingAutoTransition = null;
        }

        private void UpdateTransition()
        {
            _transitionElapsed += Time.deltaTime;
            float rawT = Mathf.Clamp01(_transitionElapsed / _transitionDuration);
            float t = _transitionCurve != null ? _transitionCurve.Evaluate(rawT) : rawT;

            // Lerp all modifier params
            for (int i = 0; i < _transitionTarget.modifierParams.Count; i++)
            {
                if (i < _transitionFromSnapshot.Count)
                {
                    ModifierParam.LerpAndApply(_transitionFromSnapshot[i], _transitionTarget.modifierParams[i], t);
                }
            }

            // Transition complete
            if (rawT >= 1f)
            {
                CompleteTransition();
            }
        }

        private void CompleteTransition()
        {
            // Clear transition state BEFORE firing events: a listener may
            // re-entrantly call SetState, and clearing afterwards would wipe
            // the new transition's target (NRE in the next UpdateTransition).
            ModifierState target = _transitionTarget;
            _isTransitioning = false;
            _transitionFromSnapshot = null;
            _transitionTarget = null;
            ApplyStateImmediate(target);
        }

        private void ApplyStateImmediate(ModifierState state)
        {
            // Apply all params and enabled states
            foreach (ModifierParam param in state.modifierParams)
            {
                if (param.target == null) continue;
                param.target.enabled = param.enabled;
                param.ApplyToTarget();
            }

            string previousState = _currentStateName;
            _currentState = state;
            _currentStateName = state.name;

            // Fire enter event
            state.onEnter?.Invoke();

            // Fire global changed event
            if (previousState != _currentStateName)
            {
                _onStateChanged?.Invoke(_currentStateName);
            }

            // An onEnter/onStateChanged listener may have re-entrantly started
            // a new transition or jumped states; scheduling this state's
            // auto-transition would clobber it.
            if (_isTransitioning || _currentState != state)
                return;

            // Check for auto-transition
            StateTransition auto = state.FindAutoTransition();
            if (auto != null)
            {
                _pendingAutoTransition = auto;
                _autoDelayTimer = 0f;
            }
            else
            {
                _pendingAutoTransition = null;
            }
        }

        private void UpdateAutoDelay()
        {
            _autoDelayTimer += Time.deltaTime;
            if (_autoDelayTimer >= _pendingAutoTransition.autoDelay)
            {
                StateTransition auto = _pendingAutoTransition;
                _pendingAutoTransition = null;
                SetState(auto.targetState);
            }
        }

        private void CancelTransition()
        {
            _isTransitioning = false;
            _transitionFromSnapshot = null;
            _transitionTarget = null;
            _pendingAutoTransition = null;
        }

        private List<ModifierParam> SnapshotCurrentParams(ModifierState referenceState)
        {
            var snapshot = new List<ModifierParam>(referenceState.modifierParams.Count);
            foreach (ModifierParam targetParam in referenceState.modifierParams)
            {
                var from = new ModifierParam { target = targetParam.target };
                from.CaptureFromTarget();
                snapshot.Add(from);
            }
            return snapshot;
        }

        private ModifierState FindState(string stateName)
        {
            for (int i = 0; i < _states.Count; i++)
            {
                if (_states[i].name == stateName)
                    return _states[i];
            }
            return null;
        }
    }
}
