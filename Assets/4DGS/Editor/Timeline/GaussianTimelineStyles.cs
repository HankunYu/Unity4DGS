using System;
using GaussianSplatting.Runtime;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    /// <summary>Colours and labels the timeline editor gives each kind of track.</summary>
    public static class GaussianTimelineStyles
    {
        public static readonly Color Background = new(0.16f, 0.16f, 0.16f);
        public static readonly Color RowEven = new(0.20f, 0.20f, 0.20f);
        public static readonly Color RowOdd = new(0.22f, 0.22f, 0.22f);
        public static readonly Color RowSelected = new(0.24f, 0.30f, 0.38f);
        public static readonly Color GroupRow = new(0.13f, 0.13f, 0.13f);
        public static readonly Color Ruler = new(0.12f, 0.12f, 0.12f);
        public static readonly Color Tick = new(1f, 1f, 1f, 0.25f);
        public static readonly Color Playhead = new(1f, 0.3f, 0.3f, 1f);
        public static readonly Color EndLine = new(1f, 1f, 1f, 0.35f);
        public static readonly Color OutOfRange = new(0f, 0f, 0f, 0.25f);
        public static readonly Color ClipOutline = new(0f, 0f, 0f, 0.6f);
        public static readonly Color ClipSelected = Color.white;
        public static readonly Color Ease = new(0f, 0f, 0f, 0.35f);
        public static readonly Color Muted = new(0.4f, 0.4f, 0.4f);

        public static Color TrackColor(GaussianTimelineTrack track)
        {
            return TrackColor(track?.GetType());
        }

        public static Color TrackColor(Type trackType)
        {
            if (trackType == typeof(GaussianChannelTrack))
                return new Color(0.27f, 0.62f, 0.60f);
            if (trackType == typeof(GaussianActivationTrack))
                return new Color(0.36f, 0.62f, 0.32f);
            if (trackType == typeof(GaussianTransformTrack))
                return new Color(0.80f, 0.52f, 0.25f);
            if (trackType == typeof(GaussianModifierStateTrack))
                return new Color(0.30f, 0.55f, 0.80f);
            if (trackType == typeof(GaussianSequenceTrack))
                return new Color(0.42f, 0.42f, 0.80f);
            if (trackType == typeof(GaussianAnimationTrack))
                return new Color(0.80f, 0.40f, 0.45f);
            if (trackType == typeof(GaussianParticleTrack))
                return new Color(0.90f, 0.62f, 0.30f);
            if (trackType == typeof(GaussianSubTimelineTrack))
                return new Color(0.58f, 0.42f, 0.78f);
            if (trackType == typeof(GaussianAudioTrack))
                return new Color(0.78f, 0.70f, 0.28f);
            if (trackType == typeof(GaussianMarkerTrack))
                return new Color(0.85f, 0.85f, 0.85f);
            return new Color(0.5f, 0.5f, 0.5f);
        }

        /// <summary>"Channel", "Modifier State"... from the track type's name.</summary>
        public static string KindName(Type type)
        {
            string name = type.Name;
            if (name.StartsWith("Gaussian"))
                name = name.Substring("Gaussian".Length);
            if (name.EndsWith("Track"))
                name = name.Substring(0, name.Length - "Track".Length);
            if (name.EndsWith("Clip"))
                name = name.Substring(0, name.Length - "Clip".Length);
            return UnityEditor.ObjectNames.NicifyVariableName(name);
        }

        /// <summary>The track's own name, or what it drives.</summary>
        public static string TrackLabel(GaussianTimelineTrack track)
        {
            if (!string.IsNullOrEmpty(track.Name))
                return track.Name;
            if (track is GaussianChannelTrack channel && !string.IsNullOrEmpty(channel.Channel))
                return channel.Channel;
            return KindName(track.GetType());
        }
    }
}
