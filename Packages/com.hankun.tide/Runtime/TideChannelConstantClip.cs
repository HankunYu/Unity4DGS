using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>Holds the channel at one value; ease it in to get there gradually.</summary>
    [Serializable]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianChannelConstantClip")]
    public class TideChannelConstantClip : TideChannelClip
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
