using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Plays the states of a <see cref="GaussianModifierStateMachine"/> as
    /// clips: each clip poses the volume's modifiers as its state does, and
    /// overlapping clips blend through each modifier's LerpParams, so enum
    /// parameters snap. While bound the state machine is switched off, so its
    /// own transitions keep out of the way.
    /// </summary>
    [Serializable]
    public class GaussianModifierStateTrack : GaussianTimelineClipTrack
    {
        public override Type BindingType => typeof(GaussianModifierStateMachine);

        public override Type ClipType => typeof(GaussianModifierStateClip);

        public override GaussianTimelineTrackInstance CreateInstance(GaussianTimelinePlayer player, Object binding)
        {
            return new Instance(this, (GaussianModifierStateMachine)binding);
        }

        // One modifier's parameters and whether it is on
        private struct Pose
        {
            public bool Enabled;
            public Vector4 P0;
            public Vector4 P1;
            public Vector4 P2;
            public Vector4 P3;

            public static Pose Of(GaussianAnimModifier modifier)
            {
                var pose = new Pose { Enabled = modifier.enabled };
                modifier.CaptureParams(out pose.P0, out pose.P1, out pose.P2, out pose.P3);
                return pose;
            }

            public static Pose Of(ModifierParam param)
            {
                return new Pose
                {
                    Enabled = param.enabled,
                    P0 = param.param0,
                    P1 = param.param1,
                    P2 = param.param2,
                    P3 = param.param3
                };
            }

            public static Pose Lerp(GaussianAnimModifier modifier, Pose from, Pose to, float t)
            {
                return new Pose
                {
                    Enabled = t < 0.5f ? from.Enabled : to.Enabled,
                    P0 = modifier.LerpParams(0, from.P0, to.P0, t),
                    P1 = modifier.LerpParams(1, from.P1, to.P1, t),
                    P2 = modifier.LerpParams(2, from.P2, to.P2, t),
                    P3 = modifier.LerpParams(3, from.P3, to.P3, t)
                };
            }

            public void ApplyTo(GaussianAnimModifier modifier)
            {
                modifier.enabled = Enabled;
                modifier.ApplyParams(P0, P1, P2, P3);
            }
        }

        private sealed class Instance : GaussianTimelineTrackInstance
        {
            private readonly GaussianModifierStateTrack _track;
            private readonly GaussianModifierStateMachine _machine;
            private readonly bool _machineEnabled;
            private readonly GaussianAnimModifier[] _modifiers;
            private readonly Pose[] _own;
            // Per clip, the pose of each modifier (its own pose where the state leaves it out)
            private readonly Dictionary<GaussianModifierStateClip, Pose[]> _clipPoses = new();
            private readonly List<GaussianClipSample> _samples = new();
            private bool _driving;

            public Instance(GaussianModifierStateTrack track, GaussianModifierStateMachine machine)
            {
                _track = track;
                _machine = machine;
                _machineEnabled = machine.enabled;
                machine.enabled = false;
                _modifiers = machine.GetComponents<GaussianAnimModifier>();
                _own = new Pose[_modifiers.Length];
                for (int i = 0; i < _modifiers.Length; i++)
                    _own[i] = Pose.Of(_modifiers[i]);
            }

            public override void Evaluate(in GaussianTimelineEvaluation evaluation)
            {
                if (_machine == null)
                    return;
                _track.Gather(evaluation, _samples);
                if (_samples.Count == 0)
                {
                    if (_driving)
                        ApplyOwn();
                    _driving = false;
                    return;
                }

                for (int m = 0; m < _modifiers.Length; m++)
                {
                    GaussianAnimModifier modifier = _modifiers[m];
                    if (modifier == null)
                        continue;
                    Pose pose = _own[m];
                    float total = 0f;
                    foreach (GaussianClipSample sample in _samples)
                    {
                        if (sample.Weight <= 0f)
                            continue;
                        Pose clipPose = PosesOf((GaussianModifierStateClip)_track.Clips[sample.ClipIndex])[m];
                        total += sample.Weight;
                        pose = total == sample.Weight ? clipPose : Pose.Lerp(modifier, pose, clipPose, sample.Weight / total);
                    }
                    if (total < 1f)
                        pose = Pose.Lerp(modifier, _own[m], pose, total);
                    pose.ApplyTo(modifier);
                }
                _driving = true;
            }

            public override void Restore()
            {
                ApplyOwn();
                if (_machine != null)
                    _machine.enabled = _machineEnabled;
            }

            public override void CollectPhaseTargets(List<GaussianAnimModifier> modifiers)
            {
                foreach (GaussianAnimModifier modifier in _modifiers)
                {
                    if (modifier != null)
                        modifiers.Add(modifier);
                }
            }

            private void ApplyOwn()
            {
                for (int m = 0; m < _modifiers.Length; m++)
                {
                    if (_modifiers[m] != null)
                        _own[m].ApplyTo(_modifiers[m]);
                }
            }

            private Pose[] PosesOf(GaussianModifierStateClip clip)
            {
                if (_clipPoses.TryGetValue(clip, out Pose[] poses))
                    return poses;

                poses = (Pose[])_own.Clone();
                ModifierState state = _machine.States.Find(s => s.name == clip.State);
                if (state == null)
                {
                    Debug.LogWarning($"Timeline track '{_track.Name}': {_machine.name} has no state '{clip.State}'", _machine);
                }
                else
                {
                    foreach (ModifierParam param in state.modifierParams)
                    {
                        int m = Array.IndexOf(_modifiers, param.target);
                        if (m >= 0)
                            poses[m] = Pose.Of(param);
                    }
                }
                _clipPoses.Add(clip, poses);
                return poses;
            }
        }
    }
}
