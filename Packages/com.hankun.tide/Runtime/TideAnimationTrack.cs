using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Object = UnityEngine.Object;
using UnityEngine.Scripting.APIUpdating;

namespace Tide
{
    /// <summary>
    /// Poses an Animator's model from animation clips at the timeline's time,
    /// so a skinned model (and anything that follows its bones) moves in
    /// step with everything else, in edit mode too. Overlapping clips
    /// crossfade by their weights. While a clip plays it replaces the
    /// Animator's own controller, which is taken off in play mode so it does
    /// not pose the model again from its own clock; between clips the
    /// controller takes over again, and in edit mode the model goes back to
    /// its own pose.
    /// </summary>
    [Serializable]
    [TideDisplay("Animation", 0.80f, 0.40f, 0.45f)]
    [MovedFrom(false, "GaussianSplatting.Runtime", "GaussianSplatting", "GaussianAnimationTrack")]
    public class TideAnimationTrack : TideClipTrack
    {
        public override Type BindingType => typeof(Animator);

        public override Type ClipType => typeof(TideAnimationClip);

        public override TideTrackInstance CreateInstance(TidePlayer player, Object binding)
        {
            return new Instance(this, (Animator)binding);
        }

        private sealed class Instance : TideTrackInstance
        {
            private readonly TideAnimationTrack _track;
            private readonly Animator _animator;
            private readonly List<TideClipSample> _samples = new();
            // The model's own pose, put back in edit mode where no
            // controller restores it
            private readonly Transform[] _transforms;
            private readonly Vector3[] _ownPositions;
            private readonly Quaternion[] _ownRotations;
            private readonly Vector3[] _ownScales;
            private PlayableGraph _graph;
            // The Animator's controller, taken off while the track drives it
            private RuntimeAnimatorController _ownController;
            private bool _controllerRemoved;
            private AnimationMixerPlayable _mixer;
            private readonly List<AnimationClip> _graphClips = new();

            public Instance(TideAnimationTrack track, Animator animator)
            {
                _track = track;
                _animator = animator;
                _transforms = animator.GetComponentsInChildren<Transform>(true);
                _ownPositions = new Vector3[_transforms.Length];
                _ownRotations = new Quaternion[_transforms.Length];
                _ownScales = new Vector3[_transforms.Length];
                for (int i = 0; i < _transforms.Length; i++)
                {
                    _ownPositions[i] = _transforms[i].localPosition;
                    _ownRotations[i] = _transforms[i].localRotation;
                    _ownScales[i] = _transforms[i].localScale;
                }
            }

            public override void Evaluate(in TideEvaluation evaluation)
            {
                if (_animator == null)
                    return;
                _track.Gather(evaluation, _samples);
                float total = 0f;
                foreach (TideClipSample sample in _samples)
                {
                    if (ClipAt(sample.ClipIndex)?.Animation != null)
                        total += sample.Weight;
                }
                if (total <= 0f)
                {
                    if (_graph.IsValid())
                        StopDriving();
                    return;
                }

                EnsureGraph();
                RemoveController();
                for (int i = 0; i < _mixer.GetInputCount(); i++)
                    _mixer.SetInputWeight(i, 0f);
                // Weights are shared out among the clips playing, so an ease
                // in fades from the clip before rather than from a rest pose
                foreach (TideClipSample sample in _samples)
                {
                    TideAnimationClip clip = ClipAt(sample.ClipIndex);
                    if (clip?.Animation == null)
                        continue;
                    _mixer.GetInput(sample.ClipIndex).SetTime(clip.AnimationTime(sample.LocalTime));
                    _mixer.SetInputWeight(sample.ClipIndex, sample.Weight / total);
                }
                _graph.Evaluate(0f);
            }

            public override void Restore()
            {
                StopDriving();
            }

            private TideAnimationClip ClipAt(int index) => _track.Clips[index] as TideAnimationClip;

            // One mixer input per clip on the track, so clips sharing an
            // animation still play it at their own times
            private void EnsureGraph()
            {
                if (_graph.IsValid() && GraphMatchesClips())
                    return;
                if (_graph.IsValid())
                    _graph.Destroy();

                _graphClips.Clear();
                foreach (TideClip clip in _track.Clips)
                    _graphClips.Add((clip as TideAnimationClip)?.Animation);
                _graph = PlayableGraph.Create($"{_animator.name} Timeline Animation");
                _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);
                _mixer = AnimationMixerPlayable.Create(_graph, _graphClips.Count);
                for (int i = 0; i < _graphClips.Count; i++)
                {
                    if (_graphClips[i] == null)
                        continue;
                    var playable = AnimationClipPlayable.Create(_graph, _graphClips[i]);
                    playable.SetApplyFootIK(false);
                    _graph.Connect(playable, 0, _mixer, i);
                }
                var output = AnimationPlayableOutput.Create(_graph, "Animation", _animator);
                output.SetSourcePlayable(_mixer);
            }

            private bool GraphMatchesClips()
            {
                if (_graphClips.Count != _track.Clips.Count)
                    return false;
                for (int i = 0; i < _graphClips.Count; i++)
                {
                    if (_graphClips[i] != (_track.Clips[i] as TideAnimationClip)?.Animation)
                        return false;
                }
                return true;
            }

            // In play mode the controller runs alongside the track and would
            // pose the model again from its own clock
            private void RemoveController()
            {
                if (_controllerRemoved || !Application.isPlaying)
                    return;
                _ownController = _animator.runtimeAnimatorController;
                _animator.runtimeAnimatorController = null;
                _controllerRemoved = true;
            }

            private void StopDriving()
            {
                if (_graph.IsValid())
                    _graph.Destroy();
                _graphClips.Clear();
                if (_controllerRemoved)
                {
                    if (_animator != null)
                        _animator.runtimeAnimatorController = _ownController;
                    _ownController = null;
                    _controllerRemoved = false;
                }
                if (Application.isPlaying)
                    return;
                for (int i = 0; i < _transforms.Length; i++)
                {
                    Transform t = _transforms[i];
                    if (t == null)
                        continue;
                    t.localPosition = _ownPositions[i];
                    t.localRotation = _ownRotations[i];
                    t.localScale = _ownScales[i];
                }
            }
        }
    }
}
