using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>Takes the channel from one value to another over the clip, along a 0–1 shape curve.</summary>
    [Serializable]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianChannelRampClip")]
    public class TideChannelRampClip : TideChannelClip
    {
        [SerializeField] private float from;
        [SerializeField] private float to = 1f;

        [Tooltip("Progress over the clip (0–1 in, 0–1 out); linear by default")]
        [SerializeField] private AnimationCurve shape = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        public float From
        {
            get => from;
            set => from = value;
        }

        public float To
        {
            get => to;
            set => to = value;
        }

        public AnimationCurve Shape
        {
            get => shape;
            set => shape = value;
        }

        public override string DisplayName =>
            string.IsNullOrEmpty(Name) ? $"{from:0.###} → {to:0.###}" : Name;

        public override float Evaluate(double localTime)
        {
            float t = (float)(localTime / Duration);
            if (shape != null && shape.length > 0)
                t = shape.Evaluate(t);
            return Mathf.LerpUnclamped(from, to, t);
        }
    }
}
