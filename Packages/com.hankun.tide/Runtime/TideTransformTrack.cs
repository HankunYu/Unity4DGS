using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>
    /// Poses a transform (local position, rotation, scale) from its clips,
    /// mixing overlaps by weight and the transform's own pose into what the
    /// weights leave. Parts switched off here are left to whatever else moves
    /// the transform.
    /// </summary>
    [Serializable]
    [TideDisplay("Transform", 0.80f, 0.52f, 0.25f)]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianTransformTrack")]
    public class TideTransformTrack : TideClipTrack
    {
        [SerializeField] private bool position = true;
        [SerializeField] private bool rotation = true;
        [SerializeField] private bool scale = true;

        public bool Position
        {
            get => position;
            set => position = value;
        }

        public bool Rotation
        {
            get => rotation;
            set => rotation = value;
        }

        public bool Scale
        {
            get => scale;
            set => scale = value;
        }

        public override Type BindingType => typeof(Transform);

        public override Type ClipType => typeof(TideTransformClip);

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            return new Instance(this, (Transform)binding);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly TideTransformTrack _track;
            private readonly Transform _target;
            private readonly Vector3 _ownPosition;
            private readonly Quaternion _ownRotation;
            private readonly Vector3 _ownScale;
            private readonly List<TideClipSample> _samples = new();
            private bool _driving;

            public Instance(TideTransformTrack track, Transform target)
            {
                _track = track;
                _target = target;
                _ownPosition = target.localPosition;
                _ownRotation = target.localRotation;
                _ownScale = target.localScale;
            }

            public override void Evaluate(in TideEvaluation evaluation)
            {
                if (_target == null)
                    return;
                _track.Gather(evaluation, _samples);
                if (_samples.Count == 0)
                {
                    if (_driving)
                        Restore();
                    _driving = false;
                    return;
                }

                Vector3 position = Vector3.zero;
                Quaternion rotation = Quaternion.identity;
                Vector3 scale = Vector3.one;
                float total = 0f;
                foreach (TideClipSample sample in _samples)
                {
                    if (sample.Weight <= 0f)
                        continue;
                    ((TideTransformClip)_track.Clips[sample.ClipIndex]).Evaluate(sample.LocalTime,
                        out Vector3 clipPosition, out Quaternion clipRotation, out Vector3 clipScale);
                    total += sample.Weight;
                    float t = sample.Weight / total;
                    position = Vector3.LerpUnclamped(position, clipPosition, t);
                    rotation = Quaternion.Slerp(rotation, clipRotation, t);
                    scale = Vector3.LerpUnclamped(scale, clipScale, t);
                }
                if (total < 1f)
                {
                    position = Vector3.LerpUnclamped(_ownPosition, position, total);
                    rotation = Quaternion.Slerp(_ownRotation, rotation, total);
                    scale = Vector3.LerpUnclamped(_ownScale, scale, total);
                }

                if (_track.position)
                    _target.localPosition = position;
                if (_track.rotation)
                    _target.localRotation = rotation;
                if (_track.scale)
                    _target.localScale = scale;
                _driving = true;
            }

            public override void Restore()
            {
                if (_target == null)
                    return;
                if (_track.position)
                    _target.localPosition = _ownPosition;
                if (_track.rotation)
                    _target.localRotation = _ownRotation;
                if (_track.scale)
                    _target.localScale = _ownScale;
            }
        }
    }
}
