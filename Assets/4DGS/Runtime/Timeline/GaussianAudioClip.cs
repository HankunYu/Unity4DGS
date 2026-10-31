using System;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>A clip on a <see cref="GaussianAudioTrack"/>: a sound and its volume. Easing fades the volume.</summary>
    [Serializable]
    public class GaussianAudioClip : GaussianTimelineClip
    {
        [SerializeField] private AudioClip audio;

        [Range(0f, 1f)]
        [SerializeField] private float volume = 1f;

        public AudioClip Audio
        {
            get => audio;
            set => audio = value;
        }

        public float Volume
        {
            get => volume;
            set => volume = Mathf.Clamp01(value);
        }

        public override string DisplayName =>
            !string.IsNullOrEmpty(Name) ? Name : audio != null ? audio.name : "No Audio";
    }
}
