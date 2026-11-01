using System;
using UnityEngine;

namespace Tide
{
    /// <summary>
    /// A span of time on a clip track. The clip fades in and out over its
    /// ease times, and its track mixes the clips that overlap by these weights,
    /// so overlapping two clips crossfades them. Subclasses add what the clip
    /// holds; the track decides what it means.
    /// </summary>
    [Serializable]
    public abstract class TideClip
    {
        public const double MinDuration = 1.0 / 120.0;

        [SerializeField] private string name;
        [SerializeField] private double start;
        [SerializeField] private double duration = 1.0;
        [SerializeField] private double easeIn;
        [SerializeField] private double easeOut;
        [SerializeField] private TideClipExtrapolation preExtrapolation;
        [SerializeField] private TideClipExtrapolation postExtrapolation;

        public string Name
        {
            get => name;
            set => name = value;
        }

        public double Start
        {
            get => start;
            set => start = Math.Max(0.0, value);
        }

        public double Duration
        {
            get => duration;
            set
            {
                duration = Math.Max(MinDuration, value);
                easeIn = Math.Min(easeIn, duration);
                easeOut = Math.Min(easeOut, duration - easeIn);
            }
        }

        public double End => start + duration;

        public double EaseIn
        {
            get => easeIn;
            set => easeIn = Math.Clamp(value, 0.0, duration - easeOut);
        }

        public double EaseOut
        {
            get => easeOut;
            set => easeOut = Math.Clamp(value, 0.0, duration - easeIn);
        }

        public TideClipExtrapolation PreExtrapolation
        {
            get => preExtrapolation;
            set => preExtrapolation = value;
        }

        public TideClipExtrapolation PostExtrapolation
        {
            get => postExtrapolation;
            set => postExtrapolation = value;
        }

        /// <summary>Label for the clip when it has no name of its own.</summary>
        public virtual string DisplayName => string.IsNullOrEmpty(name) ? GetType().Name : name;

        /// <summary>
        /// The clip's weight and clip-local time at <paramref name="time"/>,
        /// or false when the clip has no say there. A clip covers
        /// [start, end), so the next clip takes over on the frame this one
        /// ends; <paramref name="includeEnd"/> keeps the end too (for a clip
        /// that ends with the timeline). Extrapolation reaches back to
        /// <paramref name="gapStart"/> and forward to <paramref name="gapEnd"/>.
        /// </summary>
        public bool Sample(double time, double gapStart, double gapEnd, bool includeEnd, out float weight, out double localTime)
        {
            double local = time - start;
            weight = 0f;
            localTime = 0.0;
            if (local < 0.0)
            {
                if (preExtrapolation == TideClipExtrapolation.None || time < gapStart)
                    return false;
                localTime = Extrapolate(local, preExtrapolation);
                weight = Envelope(0.0);
            }
            else if (local > duration || local == duration && !includeEnd)
            {
                if (postExtrapolation == TideClipExtrapolation.None || time >= gapEnd)
                    return false;
                localTime = Extrapolate(local, postExtrapolation);
                weight = Envelope(duration);
            }
            else
            {
                localTime = local;
                weight = Envelope(local);
            }
            return true;
        }

        // Ease-in times ease-out, smoothstepped
        private float Envelope(double local)
        {
            double w = 1.0;
            if (easeIn > 0.0 && local < easeIn)
                w *= SmoothStep(local / easeIn);
            if (easeOut > 0.0 && local > duration - easeOut)
                w *= SmoothStep((duration - local) / easeOut);
            return (float)w;
        }

        private double Extrapolate(double local, TideClipExtrapolation mode)
        {
            switch (mode)
            {
                case TideClipExtrapolation.Loop:
                    return Repeat(local, duration);
                case TideClipExtrapolation.PingPong:
                    double t = Repeat(local, duration * 2.0);
                    return t > duration ? duration * 2.0 - t : t;
                default:
                    return Math.Clamp(local, 0.0, duration);
            }
        }

        private static double Repeat(double t, double length)
        {
            double r = t % length;
            return r < 0.0 ? r + length : r;
        }

        private static double SmoothStep(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }
    }
}
