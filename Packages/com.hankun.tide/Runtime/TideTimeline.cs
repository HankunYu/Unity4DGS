using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tide
{
    /// <summary>
    /// A sequence of tracks over time, played by a
    /// <see cref="TidePlayer"/> that binds each track to something
    /// in its scene. The asset holds no scene references, so one timeline can
    /// play in several places.
    /// </summary>
    [CreateAssetMenu(menuName = "Tide/Timeline", fileName = "Timeline")]
    public class TideTimeline : ScriptableObject
    {
        [Tooltip("Length in seconds; where playback holds, loops or stops")]
        [SerializeField] private double duration = 10.0;

        [Tooltip("Frames per second the editor snaps to")]
        [Min(1f)]
        [SerializeField] private float frameRate = 30f;

        [SerializeReference] private List<TideTrack> tracks = new();

        public double Duration
        {
            get => duration;
            set => duration = Math.Max(TideClip.MinDuration, value);
        }

        public float FrameRate
        {
            get => frameRate;
            set => frameRate = Mathf.Max(1f, value);
        }

        public IReadOnlyList<TideTrack> Tracks => tracks;

        public void AddTrack(TideTrack track)
        {
            if (track == null)
                throw new ArgumentNullException(nameof(track));
            tracks.Add(track);
        }

        public void InsertTrack(int index, TideTrack track)
        {
            if (track == null)
                throw new ArgumentNullException(nameof(track));
            tracks.Insert(Mathf.Clamp(index, 0, tracks.Count), track);
        }

        public bool RemoveTrack(TideTrack track)
        {
            return tracks.Remove(track);
        }

        public TideTrack FindTrack(string id)
        {
            return tracks.Find(t => t != null && t.Id == id);
        }

        // Null entries (a track whose class failed to load) are kept, so the
        // data comes back once the class does; players and editors skip them
        private void OnValidate()
        {
            duration = Math.Max(TideClip.MinDuration, duration);
        }
    }
}
