using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Moves a transform from one local pose to another over the clip, along
    /// a 0–1 shape curve. Rotations go through Euler angles, so a clip can
    /// spin more than half a turn.
    /// </summary>
    [Serializable]
    public class GaussianTransformClip : GaussianTimelineClip
    {
        [SerializeField] private Vector3 fromPosition;
        [SerializeField] private Vector3 toPosition;
        [SerializeField] private Vector3 fromRotation;
        [SerializeField] private Vector3 toRotation;
        [SerializeField] private Vector3 fromScale = Vector3.one;
        [SerializeField] private Vector3 toScale = Vector3.one;

        [Tooltip("Progress over the clip (0–1 in, 0–1 out); linear by default")]
        [SerializeField] private AnimationCurve shape = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        public Vector3 FromPosition
        {
            get => fromPosition;
            set => fromPosition = value;
        }

        public Vector3 ToPosition
        {
            get => toPosition;
            set => toPosition = value;
        }

        /// <summary>Euler angles in degrees.</summary>
        public Vector3 FromRotation
        {
            get => fromRotation;
            set => fromRotation = value;
        }

        /// <summary>Euler angles in degrees.</summary>
        public Vector3 ToRotation
        {
            get => toRotation;
            set => toRotation = value;
        }

        public Vector3 FromScale
        {
            get => fromScale;
            set => fromScale = value;
        }

        public Vector3 ToScale
        {
            get => toScale;
            set => toScale = value;
        }

        public AnimationCurve Shape
        {
            get => shape;
            set => shape = value;
        }

        public void Evaluate(double localTime, out Vector3 position, out Quaternion rotation, out Vector3 scale)
        {
            float t = (float)(localTime / Duration);
            if (shape != null && shape.length > 0)
                t = shape.Evaluate(t);
            position = Vector3.LerpUnclamped(fromPosition, toPosition, t);
            rotation = Quaternion.Euler(Vector3.LerpUnclamped(fromRotation, toRotation, t));
            scale = Vector3.LerpUnclamped(fromScale, toScale, t);
        }
    }
}
