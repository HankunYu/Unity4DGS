using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>A named point in time on a <see cref="GaussianMarkerTrack"/>.</summary>
    [Serializable]
    public class GaussianTimelineMarker
    {
        [SerializeField] private double time;
        [SerializeField] private string name;

        public double Time
        {
            get => time;
            set => time = Math.Max(0.0, value);
        }

        public string Name
        {
            get => name;
            set => name = value;
        }
    }
}
