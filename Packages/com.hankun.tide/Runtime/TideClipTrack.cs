using System;
using System.Collections.Generic;
using UnityEngine;

namespace Tide
{
    /// <summary>
    /// A track made of clips. The clips are kept in start order; a clip's
    /// pre-extrapolation fills the gap before it only when the clip before
    /// does not already hold or loop into it.
    /// </summary>
    [Serializable]
    public abstract class TideClipTrack : TideTrack
    {
        [SerializeReference] private List<TideClip> clips = new();

        public IReadOnlyList<TideClip> Clips => clips;

        /// <summary>Base class of the clips this track holds.</summary>
        public abstract Type ClipType { get; }

        public double End
        {
            get
            {
                double end = 0.0;
                foreach (TideClip clip in clips)
                {
                    if (clip != null)
                        end = Math.Max(end, clip.End);
                }
                return end;
            }
        }

        public void AddClip(TideClip clip)
        {
            if (clip == null || !ClipType.IsInstanceOfType(clip))
                throw new ArgumentException($"{GetType().Name} holds {ClipType.Name} clips, not {clip?.GetType().Name}");
            clips.Add(clip);
            SortClips();
        }

        public bool RemoveClip(TideClip clip)
        {
            return clips.Remove(clip);
        }

        /// <summary>
        /// Puts the clips back in start order after one was moved. Null
        /// entries (a clip whose class failed to load) go last and are kept.
        /// </summary>
        public void SortClips()
        {
            // Stable, so clips starting together keep their order
            var sorted = new List<TideClip>(clips);
            sorted.Sort((a, b) =>
            {
                if (a == null || b == null)
                    return a == null ? (b == null ? clips.IndexOf(a).CompareTo(clips.IndexOf(b)) : 1) : -1;
                int byStart = a.Start.CompareTo(b.Start);
                return byStart != 0 ? byStart : clips.IndexOf(a).CompareTo(clips.IndexOf(b));
            });
            clips.Clear();
            clips.AddRange(sorted);
        }

        /// <summary>
        /// Fills <paramref name="samples"/> with the clips that have a say at
        /// the evaluated time, in track order.
        /// </summary>
        public void Gather(in TideEvaluation evaluation, List<TideClipSample> samples)
        {
            double time = evaluation.Time;
            samples.Clear();
            double previousEnd = double.NegativeInfinity;
            bool previousExtends = false;
            for (int i = 0; i < clips.Count; i++)
            {
                TideClip clip = clips[i];
                if (clip == null)
                    continue;
                double gapStart = previousExtends ? double.PositiveInfinity : previousEnd;
                double gapEnd = i + 1 < clips.Count && clips[i + 1] != null ? clips[i + 1].Start : double.PositiveInfinity;
                bool includeEnd = clip.End >= evaluation.Duration;
                if (clip.Sample(time, gapStart, gapEnd, includeEnd, out float weight, out double localTime))
                    samples.Add(new TideClipSample(i, weight, localTime));
                if (clip.End >= previousEnd)
                {
                    previousEnd = clip.End;
                    previousExtends = clip.PostExtrapolation != TideClipExtrapolation.None;
                }
            }
        }

        /// <summary>Index into <paramref name="samples"/> of the heaviest one, or -1 when none weighs anything.</summary>
        public static int Strongest(List<TideClipSample> samples)
        {
            int strongest = -1;
            float strongestWeight = 0f;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].Weight > strongestWeight)
                {
                    strongest = i;
                    strongestWeight = samples[i].Weight;
                }
            }
            return strongest;
        }
    }
}
