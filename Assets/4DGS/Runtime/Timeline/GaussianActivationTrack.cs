using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Keeps a GameObject active during its clips and inactive between them.
    /// Unbinding puts back whether it was active.
    /// </summary>
    [Serializable]
    public class GaussianActivationTrack : GaussianTimelineClipTrack
    {
        public override Type BindingType => typeof(GameObject);

        public override Type ClipType => typeof(GaussianActivationClip);

        public override GaussianTimelineTrackInstance CreateInstance(GaussianTimelinePlayer player, Object binding)
        {
            var target = (GameObject)binding;
            if (player.transform.IsChildOf(target.transform))
            {
                Debug.LogWarning($"Timeline track '{Name}': {target.name} holds the player that would switch it off", target);
                return null;
            }
            return new Instance(this, target);
        }

        private sealed class Instance : GaussianTimelineTrackInstance
        {
            private readonly GaussianActivationTrack _track;
            private readonly GameObject _target;
            private readonly bool _ownActive;
            private readonly List<GaussianClipSample> _samples = new();

            public Instance(GaussianActivationTrack track, GameObject target)
            {
                _track = track;
                _target = target;
                _ownActive = target.activeSelf;
            }

            public override void Evaluate(in GaussianTimelineEvaluation evaluation)
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
