using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>A clip on a <see cref="GaussianStyleTrack"/>: a style and how strongly it applies.</summary>
    [Serializable]
    public class GaussianStyleClip : GaussianTimelineClip
    {
        [SerializeField] private GaussianSplatStyle style;

        [Range(0f, 1f)]
        [SerializeField] private float weight = 1f;

        public GaussianSplatStyle Style
        {
            get => style;
            set => style = value;
        }

        public float Weight
        {
            get => weight;
            set => weight = Mathf.Clamp01(value);
        }

        public override string DisplayName =>
            !string.IsNullOrEmpty(Name) ? Name : style != null ? style.name : "No Style";
    }
}
