using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Answers a <see cref="GaussianMarkerTrack"/>'s markers with scene
    /// events, by marker name. The timeline asset cannot reference the scene,
    /// so the reactions live here.
    /// </summary>
    public class GaussianMarkerReceiver : MonoBehaviour
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
