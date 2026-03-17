using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Defines a transition rule from one state to another,
    /// including tween duration, easing, and optional auto-trigger.
    /// </summary>
    [Serializable]
    public class StateTransition
    {
        [Tooltip("Name of the target state to transition to")]
        public string targetState;

        [Tooltip("Duration of the tween interpolation in seconds")]
        [Min(0f)]
        public float duration = 0.5f;

        [Tooltip("Easing curve for the transition (0->1)")]
        public AnimationCurve easeCurve = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

        [Tooltip("If true, this transition triggers automatically after autoDelay seconds")]
        public bool autoTransition;

        [Tooltip("Seconds to wait before auto-triggering (only if autoTransition is true)")]
        [Min(0f)]
        public float autoDelay;
    }
}
