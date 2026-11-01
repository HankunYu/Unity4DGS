using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Tide
{
    /// <summary>
    /// One row of a timeline. The asset never references the scene: a player
    /// keeps a binding per track id and hands it to
    /// <see cref="CreateInstance"/> when it plays.
    /// </summary>
    [Serializable]
    public abstract class TideTrack
    {
        [SerializeField] private string id = NewId();
        [SerializeField] private string name;
        [SerializeField] private bool muted;

        public string Id => id;

        public string Name
        {
            get => name;
            set => name = value;
        }

        public bool Muted
        {
            get => muted;
            set => muted = value;
        }

        /// <summary>What the player binds this track to (a component type or GameObject).</summary>
        public abstract Type BindingType { get; }

        /// <summary>Whether the track still plays without a binding.</summary>
        public virtual bool BindingOptional => false;

        public virtual bool CanBind(Object target)
        {
            return target != null && BindingType.IsInstanceOfType(target);
        }

        /// <summary>The track bound to <paramref name="binding"/>, or null when it cannot play on it.</summary>
        public abstract TideTrackInstance CreateInstance(TidePlayer player, Object binding);

        /// <summary>Gives a duplicated track an id of its own.</summary>
        public void RegenerateId()
        {
            id = NewId();
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }
    }
}
