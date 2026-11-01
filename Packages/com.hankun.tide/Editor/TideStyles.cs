using System;
using UnityEngine;

namespace Tide.Editor
{
    /// <summary>Colours and labels the timeline editor gives each kind of track.</summary>
    public static class TideStyles
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

        public static Color TrackColor(TideTrack track)
        {
            return TrackColor(track?.GetType());
        }

        public static Color TrackColor(Type trackType)
        {
            TideDisplayAttribute display = Display(trackType);
            return display != null && display.HasColor ? display.Color : new Color(0.5f, 0.5f, 0.5f);
        }

        /// <summary>
        /// "Channel", "Modifier State"... from the type's <see cref="TideDisplayAttribute"/>,
        /// or else its name without a "Tide" prefix and the "Track"/"Clip" suffix.
        /// </summary>
        public static string KindName(Type type)
        {
            TideDisplayAttribute display = Display(type);
            if (!string.IsNullOrEmpty(display?.Name))
                return display.Name;
            string name = type.Name;
            if (name.StartsWith("Tide"))
                name = name.Substring("Tide".Length);
            if (name.EndsWith("Track"))
                name = name.Substring(0, name.Length - "Track".Length);
            if (name.EndsWith("Clip"))
                name = name.Substring(0, name.Length - "Clip".Length);
            return UnityEditor.ObjectNames.NicifyVariableName(name);
        }

        private static TideDisplayAttribute Display(Type type) =>
            type != null ? (TideDisplayAttribute)Attribute.GetCustomAttribute(type, typeof(TideDisplayAttribute), false) : null;

        /// <summary>The track's own name, or what it drives.</summary>
        public static string TrackLabel(TideTrack track)
        {
            if (!string.IsNullOrEmpty(track.Name))
                return track.Name;
            if (track is TideChannelTrack channel && !string.IsNullOrEmpty(channel.Channel))
                return channel.Channel;
            return KindName(track.GetType());
        }
    }
}
