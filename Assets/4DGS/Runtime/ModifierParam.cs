using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Snapshot of a single modifier's parameters and enabled state.
    /// Used by ModifierState to define target values for state machine transitions.
    /// </summary>
    [Serializable]
    public class ModifierParam
    {
        [Tooltip("Reference to the modifier component on the volume")]
        public GaussianAnimModifier target;

        [Tooltip("Whether this modifier should be active in this state")]
        public bool enabled = true;

        public Vector4 param0;
        public Vector4 param1;
        public Vector4 param2;
        public Vector4 param3;

        /// <summary>
        /// Capture current parameter values from the target modifier.
        /// </summary>
        public void CaptureFromTarget()
        {
            if (target == null) return;
            enabled = target.enabled;
            target.CaptureParams(out param0, out param1, out param2, out param3);
        }

        /// <summary>
        /// Apply stored parameter values to the target modifier.
        /// </summary>
        public void ApplyToTarget()
        {
            if (target == null) return;
            target.ApplyParams(param0, param1, param2, param3);
        }

        /// <summary>
        /// Linearly interpolate between two snapshots and apply to target.
        /// </summary>
        public static void LerpAndApply(ModifierParam from, ModifierParam to, float t)
        {
            if (to.target == null) return;
            to.target.ApplyParams(
                Vector4.Lerp(from.param0, to.param0, t),
                Vector4.Lerp(from.param1, to.param1, t),
                Vector4.Lerp(from.param2, to.param2, t),
                Vector4.Lerp(from.param3, to.param3, t)
            );
        }
    }
}
