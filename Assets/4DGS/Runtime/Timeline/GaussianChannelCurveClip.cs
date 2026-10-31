using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>Plays a curve on the channel, keyed in clip-local seconds.</summary>
    [Serializable]
    public class GaussianChannelCurveClip : GaussianChannelClip
    {
        [SerializeField] private AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

        public AnimationCurve Curve
        {
            get => curve;
            set => curve = value;
        }

        public override float Evaluate(double localTime)
        {
            return curve != null && curve.length > 0 ? curve.Evaluate((float)localTime) : 0f;
        }
    }
}
