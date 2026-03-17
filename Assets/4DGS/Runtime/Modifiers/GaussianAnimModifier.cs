// SPDX-License-Identifier: MIT

using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Base class for all Gaussian Splat animation modifiers.
    /// Each modifier defines a type ID and packs its parameters into 4 Vector4s
    /// for upload to the GPU animation compute shader.
    /// </summary>
    public abstract class GaussianAnimModifier : MonoBehaviour
    {
        public const int TypeDissolve = 0;
        public const int TypeWave = 1;
        public const int TypeWarp = 2;
        public const int TypeProperty = 3;
        public const int TypeCaustic = 4;
        public const int TypeWheatWave = 5;
        public const int TypeTurbulence = 6;

        public const int ParamSlotCount = 16;

        public abstract int ModifierType { get; }

        /// <summary>
        /// Returns 16 human-readable labels for the parameter slots (4 Vector4s × 4 components).
        /// Empty string means the slot is unused. Used by editors to display meaningful names
        /// instead of generic p0.x, p0.y, etc.
        /// Labels must match the packing order of <see cref="CaptureParams"/>.
        /// </summary>
        public virtual string[] GetParamLabels() => new string[ParamSlotCount];

        /// <summary>
        /// Pack modifier parameters into 4 Vector4s for GPU upload.
        /// Each modifier subclass interprets these differently.
        /// </summary>
        public abstract void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3);

        /// <summary>
        /// Capture current serialized field values into 4 Vector4s for state machine snapshots.
        /// Unlike <see cref="FillParams"/>, this captures raw field values without time injection
        /// or normalization, making them suitable for interpolation between states.
        /// </summary>
        public abstract void CaptureParams(out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3);

        /// <summary>
        /// Apply parameter values from a state machine snapshot back to serialized fields.
        /// Values may be interpolated between states, so enum-like or discrete values
        /// should be rounded to the nearest valid value before assignment.
        /// </summary>
        public abstract void ApplyParams(Vector4 p0, Vector4 p1, Vector4 p2, Vector4 p3);
    }
}
