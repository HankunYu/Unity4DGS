using System;
using UnityEngine;

namespace Tide
{
    /// <summary>A named point in time on a <see cref="TideMarkerTrack"/>.</summary>
    [Serializable]
    public class TideMarker
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
