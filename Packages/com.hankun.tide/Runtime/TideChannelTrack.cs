using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>
    /// Drives one named channel of an <see cref="ITideChannelTarget"/>
    /// (a material parameter, a light's intensity, an effect's weight...).
    /// Overlapping clips mix by weight; where their weights add up to less
    /// than one the channel's own value makes up the rest. Outside every clip
    /// the channel gets its own value back once and is then left alone.
    /// </summary>
    [Serializable]
    [TideDisplay("Channel", 0.27f, 0.62f, 0.60f)]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianChannelTrack")]
    public class TideChannelTrack : TideClipTrack
    {
        [Tooltip("Name of the channel on the bound target")]
        [SerializeField] private string channel;

        public string Channel
        {
            get => channel;
            set => channel = value;
        }

        public override Type BindingType => typeof(Component);

        public override Type ClipType => typeof(TideChannelClip);

        public override bool CanBind(Object target)
        {
            return target is Component && target is ITideChannelTarget;
        }

        /// <summary>Index of <paramref name="channelName"/> on <paramref name="target"/>, or -1.</summary>
        public static int FindChannel(ITideChannelTarget target, string channelName)
        {
            for (int i = 0; i < target.ChannelCount; i++)
            {
                if (target.GetChannelName(i) == channelName)
                    return i;
            }
            return -1;
        }

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            var target = (ITideChannelTarget)binding;
            int index = FindChannel(target, channel);
            if (index < 0)
            {
                Debug.LogWarning($"Timeline track '{Name}': {binding.name} has no channel '{channel}'", binding);
                return null;
            }
            return new Instance(this, target, binding, index);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly TideChannelTrack _track;
            private readonly ITideChannelTarget _target;
            private readonly Object _targetObject;
            private readonly int _channel;
            private readonly float _ownValue;
            private readonly List<TideClipSample> _samples = new();
            private bool _driving;

            public Instance(TideChannelTrack track, ITideChannelTarget target, Object targetObject, int channel)
            {
                _track = track;
                _target = target;
                _targetObject = targetObject;
                _channel = channel;
                _ownValue = target.GetChannelValue(channel);
            }

            public override void Evaluate(in TideEvaluation evaluation)
            {
                if (_targetObject == null)
                    return;
                _track.Gather(evaluation, _samples);

                // Mix pairwise through BlendChannel, so discrete channels snap
                float value = 0f;
                float total = 0f;
                foreach (TideClipSample sample in _samples)
                {
                    if (sample.Weight <= 0f)
                        continue;
                    float clipValue = ((TideChannelClip)_track.Clips[sample.ClipIndex]).Evaluate(sample.LocalTime);
                    total += sample.Weight;
                    value = total == sample.Weight
                        ? clipValue
                        : _target.BlendChannel(_channel, value, clipValue, sample.Weight / total);
                }

                if (_samples.Count == 0)
                {
                    if (_driving)
                        _target.SetChannelValue(_channel, _ownValue);
                    _driving = false;
                    return;
                }
                if (total < 1f)
                    value = _target.BlendChannel(_channel, _ownValue, value, total);
                _target.SetChannelValue(_channel, value);
                _driving = true;
            }

            public override void Restore()
            {
                if (_targetObject != null)
                    _target.SetChannelValue(_channel, _ownValue);
            }

            public override void CollectPhaseTargets(List<ITidePhaseTarget> targets)
            {
                if (_targetObject is ITidePhaseTarget target)
                    targets.Add(target);
            }
        }
    }
}
