using System;
using UnityEngine;

namespace GaussianSplatting.Editor.MeshSplat
{
    // Which animation frames a sequence output bakes, and how
    [Serializable]
    public class MeshSplatSequenceSettings
    {
        [Tooltip("Animation to bake. Empty uses the model's first clip.")]
        public AnimationClip clip;
        [Tooltip("Frames per second to bake at, and the sequence's playback rate.")]
        [Min(0.01f)] public float frameRate = 30f;
        [Tooltip("First frame to bake, at Frame Rate from the clip's start.")]
        [Min(0)] public int firstFrame;
        [Tooltip("Last frame to bake (inclusive). -1 bakes to the end of the clip.")]
        [Min(-1)] public int lastFrame = -1;
        [Tooltip("Sample every frame anew in model space: scanners and sampling stay fixed while the model moves " +
                 "through them, so splats slide over the surface. Off samples once in the bind pose and carries " +
                 "the splats along with the surface, like GaussianMeshDriver.")]
        public bool resampleEveryFrame;
    }
}
