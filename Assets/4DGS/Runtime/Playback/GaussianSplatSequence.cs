using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // An ordered set of splat assets played back as a 4D sequence by
    // GaussianPlayer. Frames are referenced directly, so only sequences in
    // use end up in a build.
    [CreateAssetMenu(menuName = "Gaussian Splatting/Sequence", fileName = "Sequence", order = 1)]
    public class GaussianSplatSequence : ScriptableObject
    {
        public GaussianSplatAsset[] frames = new GaussianSplatAsset[0];

        // Frames per second the sequence was captured at
        [Min(0.01f)] public float frameRate = 30f;

        // The folder the frames were collected from, for refreshing them in
        // the editor; empty when the list was made by hand
        public string sourceFolderGuid;

        public int FrameCount => frames != null ? frames.Length : 0;
    }
}
