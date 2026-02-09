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

        public abstract int ModifierType { get; }

        /// <summary>
        /// Pack modifier parameters into 4 Vector4s for GPU upload.
        /// Each modifier subclass interprets these differently.
        /// </summary>
        public abstract void FillParams(float time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3);
    }
}
