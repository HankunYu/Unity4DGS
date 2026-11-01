using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>
    /// Keeps a GameObject active during its clips and inactive between them.
    /// Unbinding puts back whether it was active.
    /// </summary>
    [Serializable]
    [TideDisplay("Activation", 0.36f, 0.62f, 0.32f)]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianActivationTrack")]
    public class TideActivationTrack : TideClipTrack
    {
        public override Type BindingType => typeof(GameObject);

        public override Type ClipType => typeof(TideActivationClip);

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            var target = (GameObject)binding;
            if (player.transform.IsChildOf(target.transform))
            {
                Debug.LogWarning($"Timeline track '{Name}': {target.name} holds the player that would switch it off", target);
                return null;
            }
            return new Instance(this, target);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly TideActivationTrack _track;
            private readonly GameObject _target;
            private readonly bool _ownActive;
            private readonly List<TideClipSample> _samples = new();

            public Instance(TideActivationTrack track, GameObject target)
            {
                _track = track;
                _target = target;
                _ownActive = target.activeSelf;
            }

            public override void Evaluate(in TideEvaluation evaluation)
            {
                if (_target == null)
                    return;
                _track.Gather(evaluation, _samples);
                bool active = _samples.Count > 0;
                if (_target.activeSelf != active)
                    _target.SetActive(active);
            }

            public override void Restore()
            {
                if (_target != null && _target.activeSelf != _ownActive)
                    _target.SetActive(_ownActive);
            }
        }
    }
}
