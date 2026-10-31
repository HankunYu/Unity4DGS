using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>A clip on a <see cref="GaussianModifierStateTrack"/>: one of the bound state machine's states, by name.</summary>
    [Serializable]
    public class GaussianModifierStateClip : GaussianTimelineClip
    {
        [SerializeField] private string state;

        public string State
        {
            get => state;
            set => state = value;
        }

        public override string DisplayName =>
            !string.IsNullOrEmpty(Name) ? Name : string.IsNullOrEmpty(state) ? "No State" : state;
    }
}
