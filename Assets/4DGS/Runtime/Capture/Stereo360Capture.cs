using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Captures a 360 equirect image sequence.
    //
    // Unity Recorder's stereo 360 path is unsupported on SRP: Camera.stereoSeparation
    // is only honoured by the built-in pipeline's RenderToCubemap eye offset, so both
    // eyes come out pixel-identical under URP.
    //
    // Both projections produce one 2:1 equirect per eye, which the layout then
    // places in the output frame. Keeping that split means eye layout is orthogonal
    // to projection, instead of riding on ConvertToEquirect's half-target behaviour.
    //
    // Cubemap projection needs the GaussianSplatURPFeature "Use Render Context
    // Matrices" toggle so splats re-sort for every face render.
    [AddComponentMenu("Gaussian Splatting/Capture/Stereo 360 Capture")]
    public class Stereo360Capture : SequenceCapture
    {
        [Tooltip("Cubemap is the cheap approximation with seams at the cube edges and corners. " +
                 "OmniDirectionalStereo is correct at every azimuth but only projects splats, " +
                 "and requires the PointCloud render mode.")]
        [SerializeField] private Stereo360Projection projection = Stereo360Projection.OmniDirectionalStereo;

        [Tooltip("How the eyes are packed into the output frame. LeftEyeOnly halves the cost, for checking framing.")]
        [SerializeField] private Stereo360EyeLayout eyeLayout = Stereo360EyeLayout.OverUnder;

        [Tooltip("Interpupillary distance in meters.")]
        [SerializeField] private float eyeSeparation = 0.065f;

        [Tooltip("Resolution of each cubemap face. Cubemap projection only. " +
                 "Density matches the equirect at outputWidth/pi; far above that, the " +
                 "unfiltered minification in ConvertToEquirect starts to alias.")]
        [SerializeField] private int cubemapFaceSize = 2048;

        [Tooltip("Per-eye equirect width in pixels. Each eye is width x width/2.")]
        [SerializeField] private int outputWidth = 5760;

        public Stereo360Projection Projection => projection;

        public Vector2Int PerEyeResolution => new Vector2Int(outputWidth, outputWidth / 2);

        public int EyeCount => eyeLayout == Stereo360EyeLayout.LeftEyeOnly ? 1 : 2;

        public override Vector2Int FrameResolution
        {
            get
            {
                Vector2Int eye = PerEyeResolution;
                return eyeLayout switch
                {
                    Stereo360EyeLayout.OverUnder => new Vector2Int(eye.x, eye.y * 2),
                    Stereo360EyeLayout.SideBySide => new Vector2Int(eye.x * 2, eye.y),
                    _ => eye,
                };
            }
        }

        public override int ViewCount => EyeCount;

        protected override Vector2Int ViewResolution => PerEyeResolution;

        // Rows count from the bottom, so the left eye of an over-under frame
        // sits in the upper half of the buffer and lands on top
        protected override Vector2Int ViewOffset(int view)
        {
            Vector2Int eye = PerEyeResolution;
            return eyeLayout switch
            {
                Stereo360EyeLayout.OverUnder => new Vector2Int(0, view == 0 ? eye.y : 0),
                Stereo360EyeLayout.SideBySide => new Vector2Int(view == 0 ? 0 : eye.x, 0),
                _ => Vector2Int.zero,
            };
        }

        protected override string LogTag => "[Stereo360Capture]";

        protected override string DefaultOutputDirectory => "Recordings/Stereo360";

        protected override string SessionDescription =>
            $"{eyeLayout}. " + (projection == Stereo360Projection.OmniDirectionalStereo
                ? "ODS projection: 1 render per eye, no cubemap, no per-face seams."
                : "Cubemap projection: make sure GaussianSplatURPFeature has 'Use Render Context Matrices' enabled.");

        private RenderTexture _cube;
        // The config and its settings as they were before the frame's views
        private GaussianSplatConfig _frameConfig;
        private GaussianSplatProjectionMode _prevMode;
        private float _prevOffset;

        // The splat path sizes its quads from a screen-space covariance that ODS
        // cannot produce, so it falls back to a synthesised isotropic footprint
        // that it never clamps — a splat close to the eye then rasterises a quad
        // wide enough to hang the GPU. Refuse rather than crash.
        protected override bool ValidatePrerequisites()
        {
            if (projection != Stereo360Projection.OmniDirectionalStereo)
            {
                return true;
            }

            var config = GaussianSplatRenderSystem.instance.Config;
            if (config == null)
            {
                Debug.LogError("[Stereo360Capture] ODS capture needs an active GaussianSplatConfig in the scene.");
                return false;
            }
            if (config.renderMode != GaussianSplatRenderMode.PointCloud)
            {
                Debug.LogError("[Stereo360Capture] ODS capture requires GaussianSplatConfig.renderMode = " +
                               $"PointCloud (currently {config.renderMode}).");
                return false;
            }
            return true;
        }

        protected override bool BeginFrame()
        {
            _frameConfig = GaussianSplatRenderSystem.instance.Config;
            _prevMode = _frameConfig != null ? _frameConfig.projectionMode : GaussianSplatProjectionMode.Perspective;
            _prevOffset = _frameConfig != null ? _frameConfig.odsEyeOffset : 0f;
            return projection != Stereo360Projection.OmniDirectionalStereo || CanRenderOds(_frameConfig);
        }

        protected override void EndFrame()
        {
            if (_frameConfig != null)
            {
                _frameConfig.projectionMode = _prevMode;
                _frameConfig.odsEyeOffset = _prevOffset;
            }
            _frameConfig = null;
        }

        protected override void RenderView(int view, RenderTexture target)
        {
            // LeftEyeOnly is a preview, not half a stereo pair: render it from
            // the rig centre so the framing matches what the eyes straddle.
            float offset = EyeCount == 1 ? 0f : (view == 0 ? -0.5f : 0.5f) * eyeSeparation;

            if (projection == Stereo360Projection.OmniDirectionalStereo)
            {
                RenderEyeOds(_frameConfig, offset, target);
            }
            else
            {
                RenderEyeCubemap(offset, target);
            }
        }

        private bool CanRenderOds(GaussianSplatConfig config)
        {
            if (config == null)
            {
                Debug.LogError("[Stereo360Capture] ODS capture needs an active GaussianSplatConfig in the scene.");
                return false;
            }
            if (!RenderPipeline.SupportsRenderRequest(TargetCamera, new RenderPipeline.StandardRequest()))
            {
                Debug.LogError("[Stereo360Capture] Active render pipeline rejects StandardRequest; " +
                               "ODS capture needs it to render off-screen safely.");
                return false;
            }
            return true;
        }

        // One render covering the whole sphere: the ODS projection happens
        // per-splat in the compute shader, so no cube faces are involved and
        // there are no per-face eye-offset discontinuities.
        private void RenderEyeOds(GaussianSplatConfig config, float offset, RenderTexture target)
        {
            config.projectionMode = GaussianSplatProjectionMode.OmniDirectionalStereo;
            config.odsEyeOffset = offset;

            // Camera.Render() is the legacy built-in entry point: under an SRP it
            // re-enters the pipeline while this frame's render graph is still
            // executing, which strands GPU work and eventually deadlocks the next
            // GraphicsBuffer write. SubmitRenderRequest is the supported way to
            // drive an off-screen render from a coroutine.
            TargetCamera.SubmitRenderRequest(new RenderPipeline.StandardRequest { destination = target });
        }

        private void RenderEyeCubemap(float offset, RenderTexture target)
        {
            Camera camera = TargetCamera;
            Transform camTransform = camera.transform;
            Vector3 basePos = camTransform.position;
            Quaternion baseRot = camTransform.rotation;

            // Zero out the engine-side separation: it is a no-op under URP today, but
            // this capture must never double-apply an offset if that ever changes.
            float prevSeparation = camera.stereoSeparation;
            camera.stereoSeparation = 0f;

            for (int face = 0; face < 6; face++)
            {
                camTransform.position = basePos + baseRot * (FaceRightDir((CubemapFace)face) * offset);
                // MonoOrStereoscopicEye.Left (not Mono) so face orientation follows
                // the camera rotation; with stereoSeparation = 0 it adds no offset.
                camera.RenderToCubemap(_cube, 1 << face, Camera.MonoOrStereoscopicEye.Left);
            }

            camTransform.SetPositionAndRotation(basePos, baseRot);
            camera.stereoSeparation = prevSeparation;

            // Mono fills the whole 2:1 target, so each eye lands in its own image
            // and the layout decides where it goes.
            _cube.ConvertToEquirect(target, Camera.MonoOrStereoscopicEye.Mono);
        }

        // Horizontal right axis of each cubemap face in camera-local space; the eye
        // offset is applied along it. The vertical faces have no horizontal component
        // of their own, so they reuse the forward face's axis — the standard stereo
        // cubemap approximation (parallax degrades toward the poles).
        private static Vector3 FaceRightDir(CubemapFace face)
        {
            switch (face)
            {
                case CubemapFace.PositiveX: return new Vector3(0f, 0f, -1f);
                case CubemapFace.NegativeX: return new Vector3(0f, 0f, 1f);
                case CubemapFace.NegativeZ: return new Vector3(-1f, 0f, 0f);
                default: return new Vector3(1f, 0f, 0f);
            }
        }

        protected override void CreateViewResources()
        {
            if (projection == Stereo360Projection.Cubemap)
            {
                // One cube, reused per eye: each eye is converted to its equirect
                // immediately, so both never need to be resident at once.
                _cube = new RenderTexture(cubemapFaceSize, cubemapFaceSize, 24, RenderTextureFormat.ARGB32)
                {
                    dimension = TextureDimension.Cube
                };
                _cube.Create();
            }
        }

        protected override void ReleaseViewResources()
        {
            ReleaseRT(ref _cube);
        }
    }
}
