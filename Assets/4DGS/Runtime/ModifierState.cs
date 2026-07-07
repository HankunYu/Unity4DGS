using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Defines a named state in the modifier state machine.
    /// Contains parameter snapshots for all modifiers and transition rules.
    /// </summary>
    [Serializable]
    public class ModifierState
    {
        [Tooltip("Unique name identifying this state")]
        public string name;

        [Tooltip("Parameter snapshots for each modifier in this state")]
        public List<ModifierParam> modifierParams = new();

        [Tooltip("Transition rules from this state to other states")]
        public List<StateTransition> transitions = new();

        [Header("Events")]
        [Tooltip("Fired when the state machine enters this state (after transition completes)")]
        public UnityEvent onEnter;

        [Tooltip("Fired when the state machine exits this state (before transition starts)")]
        public UnityEvent onExit;

        /// <summary>
        /// Find a transition to the given target state, or null if none exists.
        /// </summary>
        public StateTransition FindTransitionTo(string targetStateName)
        {
            for (int i = 0; i < transitions.Count; i++)
            {
                if (transitions[i].targetState == targetStateName)
                    return transitions[i];
            }
            return null;
        }

        /// <summary>
        /// Find the first auto-transition, or null if none exists.
        /// </summary>
        public StateTransition FindAutoTransition()
        {
            for (int i = 0; i < transitions.Count; i++)
            {
                if (transitions[i].autoTransition)
                    return transitions[i];
            }
            return null;
        }
    }
}
