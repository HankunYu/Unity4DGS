using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Drives one named channel of an <see cref="IGaussianTimelineTarget"/>
    /// (a modifier parameter, a volume weight, the depth-of-field focus...).
    /// Overlapping clips mix by weight; where their weights add up to less
    /// than one the channel's own value makes up the rest. Outside every clip
    /// the channel gets its own value back once and is then left alone.
    /// </summary>
    [Serializable]
    public class GaussianChannelTrack : GaussianTimelineClipTrack
    {
        [Tooltip("Name of the channel on the bound target")]
        [SerializeField] private string channel;

        public string Channel
        {
            get => channel;
            set => channel = value;
        }

        public override Type BindingType => typeof(Component);

        public override Type ClipType => typeof(GaussianChannelClip);

        public override bool CanBind(Object target)
        {
            return target is Component && target is IGaussianTimelineTarget;
        }

        /// <summary>Index of <paramref name="channelName"/> on <paramref name="target"/>, or -1.</summary>
        public static int FindChannel(IGaussianTimelineTarget target, string channelName)
        {
            for (int i = 0; i < target.ChannelCount; i++)
            {
                if (target.GetChannelName(i) == channelName)
                    return i;
            }
            return -1;
        }

        public override GaussianTimelineTrackInstance CreateInstance(GaussianTimelinePlayer player, Object binding)
        {
            var target = (IGaussianTimelineTarget)binding;
            int index = FindChannel(target, channel);
            if (index < 0)
            {
                Debug.LogWarning($"Timeline track '{Name}': {binding.name} has no channel '{channel}'", binding);
                return null;
            }
            return new Instance(this, target, binding, index);
        }

        private sealed class Instance : GaussianTimelineTrackInstance
        {
            private readonly GaussianChannelTrack _track;
            private readonly IGaussianTimelineTarget _target;
            private readonly Object _targetObject;
            private readonly int _channel;
            private readonly float _ownValue;
            private readonly List<GaussianClipSample> _samples = new();
            private bool _driving;

            public Instance(GaussianChannelTrack track, IGaussianTimelineTarget target, Object targetObject, int channel)
            {
                _track = track;
                _target = target;
                _targetObject = targetObject;
                _channel = channel;
                _ownValue = target.GetChannelValue(channel);
            }

            public override void Evaluate(in GaussianTimelineEvaluation evaluation)
            {
                if (_targetObject == null)
                    return;
                _track.Gather(evaluation, _samples);

                // Mix pairwise through BlendChannel, so discrete channels snap
                float value = 0f;
                float total = 0f;
                foreach (GaussianClipSample sample in _samples)
                {
                    if (sample.Weight <= 0f)
                        continue;
                    float clipValue = ((GaussianChannelClip)_track.Clips[sample.ClipIndex]).Evaluate(sample.LocalTime);
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

            public override void CollectPhaseTargets(List<GaussianAnimModifier> modifiers)
            {
                if (_targetObject is GaussianAnimModifier modifier)
                    modifiers.Add(modifier);
            }
        }
    }
}
