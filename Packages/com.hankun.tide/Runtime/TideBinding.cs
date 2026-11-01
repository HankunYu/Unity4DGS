using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tide
{
    /// <summary>What a player binds one track (by id) to.</summary>
    [Serializable]
    public struct TideBinding
    {
        [SerializeField] private string trackId;
        [SerializeField] private Object target;

        public TideBinding(string trackId, Object target)
        {
            this.trackId = trackId;
            this.target = target;
        }

        public string TrackId => trackId;

        public Object Target => target;
    }
}
