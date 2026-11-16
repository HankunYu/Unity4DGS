using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Captures the camera's ordinary view as an image sequence: one render per
    // frame at the output resolution, whatever the game view's size.
    [AddComponentMenu("Gaussian Splatting/Capture/Flat Capture")]
    public class FlatCapture : SequenceCapture
    {
        [Tooltip("Frame width in pixels.")]
        [Min(16)] [SerializeField] private int outputWidth = 1920;

        [Tooltip("Frame height in pixels.")]
        [Min(16)] [SerializeField] private int outputHeight = 1080;

        public override Vector2Int FrameResolution =>
            new Vector2Int(Mathf.Max(16, outputWidth), Mathf.Max(16, outputHeight));

        public override int ViewCount => 1;

        protected override Vector2Int ViewResolution => FrameResolution;

        protected override Vector2Int ViewOffset(int view) => Vector2Int.zero;

        protected override string LogTag => "[FlatCapture]";

        protected override string DefaultOutputDirectory => "Recordings/Flat";

        protected override string SessionDescription => "Flat: the camera's own view, 1 render per frame.";

        protected override bool BeginFrame()
        {
            if (!RenderPipeline.SupportsRenderRequest(TargetCamera, new RenderPipeline.StandardRequest()))
            {
                Debug.LogError("[FlatCapture] Active render pipeline rejects StandardRequest; " +
                               "the capture needs it to render off-screen safely.");
                return false;
            }
            return true;
        }

        // Camera.Render() is the legacy built-in entry point: under an SRP it
        // re-enters the pipeline while this frame's render graph is still
        // executing. SubmitRenderRequest is the supported way to drive an
        // off-screen render from a coroutine.
        protected override void RenderView(int view, RenderTexture target)
        {
            TargetCamera.SubmitRenderRequest(new RenderPipeline.StandardRequest { destination = target });
        }
    }
}
