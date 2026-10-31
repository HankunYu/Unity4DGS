using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>What a player binds one track (by id) to.</summary>
    [Serializable]
    public struct GaussianTimelineBinding
    {
        [SerializeField] private string trackId;
        [SerializeField] private Object target;

        public GaussianTimelineBinding(string trackId, Object target)
        {
            this.trackId = trackId;
            this.target = target;
        }

        public string TrackId => trackId;

        public Object Target => target;
    }
}
