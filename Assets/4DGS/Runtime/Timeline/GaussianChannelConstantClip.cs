using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>Holds the channel at one value; ease it in to get there gradually.</summary>
    [Serializable]
    public class GaussianChannelConstantClip : GaussianChannelClip
    {
        [SerializeField] private float value;

        public float Value
        {
            get => value;
            set => this.value = value;
        }

        public override string DisplayName => string.IsNullOrEmpty(Name) ? value.ToString("0.###") : Name;

        public override float Evaluate(double localTime)
        {
            return value;
        }
    }
}
