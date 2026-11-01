using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace Tide
{
    /// <summary>
    /// Answers a <see cref="TideMarkerTrack"/>'s markers with scene
    /// events, by marker name. The timeline asset cannot reference the scene,
    /// so the reactions live here.
    /// </summary>
    [AddComponentMenu("Tide/Tide Marker Receiver")]
    public class TideMarkerReceiver : MonoBehaviour
    {
        [Serializable]
        private struct Reaction
        {
            public string marker;
            public UnityEvent onMarker;
        }

        [SerializeField] private List<Reaction> reactions = new();

        public void Receive(string marker)
        {
            foreach (Reaction reaction in reactions)
            {
                if (reaction.marker == marker)
                    reaction.onMarker?.Invoke();
            }
        }
    }
}
