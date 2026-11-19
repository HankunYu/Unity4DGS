using System;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // A reference from a component in a look's layers to the renderer the
    // look was saved from or to another object under it (an emitter's
    // Occluded By, say). A prefab cannot hold these, so the preset keeps them
    // by address and sets them again when it is applied.
    [Serializable]
    internal struct GaussianLookReference
    {
        [SerializeField] private GaussianLookAddress owner;
        [SerializeField] private string property;
        [SerializeField] private GaussianLookAddress target;

        public GaussianLookReference(GaussianLookAddress owner, string property, GaussianLookAddress target)
        {
            this.owner = owner;
            this.property = property;
            this.target = target;
        }

        public readonly GaussianLookAddress Owner => owner;

        public readonly string Property => property;

        public readonly GaussianLookAddress Target => target;
    }
}
