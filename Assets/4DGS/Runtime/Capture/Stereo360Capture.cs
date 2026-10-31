using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Captures a 360 equirect PNG sequence at end of frame.
    //
    // Unity Recorder's stereo 360 path is unsupported on SRP: Camera.stereoSeparation
    // is only honoured by the built-in pipeline's RenderToCubemap eye offset, so both
    // eyes come out pixel-identical under URP.
    //
    // Both projections produce one 2:1 equirect per eye, which the layout step then
    // packs into the output frame. Keeping that split means eye layout is orthogonal
    // to projection, instead of riding on ConvertToEquirect's half-target behaviour.
    //
    // Cubemap projection needs the GaussianSplatURPFeature "Use Render Context
    // Matrices" toggle so splats re-sort for every face render.
    public class Stereo360Capture : MonoBehaviour
    {
        [Tooltip("Camera to capture from. Defaults to the camera on this GameObject, then Camera.main.")]
        [SerializeField] private Camera targetCamera;

        [Header("Projection")]
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

        [Header("Output")]
        [Tooltip("Per-eye equirect width in pixels. Each eye is width x width/2.")]
        [SerializeField] private int outputWidth = 5760;

        [Tooltip("Capture frame rate. Locks Time.captureFramerate so no frames are dropped.")]
        [SerializeField] private int frameRate = 30;

        [Tooltip("Output directory, relative to the project root. Each run writes into a " +
                 "fresh take_NNNN subfolder, so a new run can never overwrite an old one.")]
        [SerializeField] private string outputDirectory = "Recordings/Stereo360";

        [Tooltip("PNG for a beauty pass; EXR for any AOV. An 8-bit PNG cannot carry a " +
                 "depth channel — 256 quantisation steps leave nothing usable for compositing.")]
        [SerializeField] private Stereo360OutputFormat outputFormat = Stereo360OutputFormat.Png;

        [Tooltip("Take folder to write into. 0 picks the next unused one. A specific number " +
                 "reuses that folder, overwriting same-named frames and deleting nothing — " +
                 "which is what lets a beauty run and an AOV run of the same shot share a take.")]
        [Min(0)] [SerializeField] private int takeNumber;

        [Tooltip("Leading part of each frame name: <prefix>_<number>.<ext>. Two runs sharing " +
                 "one take need different prefixes, or the second overwrites the first. " +
                 "Characters a filesystem rejects are replaced with '_'.")]
        [SerializeField] private string filenamePrefix = DefaultFilenamePrefix;

        [Tooltip("Digits in the frame number. ffmpeg's %0Nd pattern must match this, and " +
                 "4 digits stops working past 9999 frames.")]
        [Range(3, 8)] [SerializeField] private int filenamePadding = 4;

        [Header("Timing")]
        [Tooltip("Start capturing as soon as play mode begins.")]
        [SerializeField] private bool captureOnStart;

        [Tooltip("Stop automatically after this many frames. 0 = capture until StopCapture.")]
        [SerializeField] private int frameLimit;

        [Tooltip("Discard this many frames before writing anything, to let sorting and " +
                 "animation settle after entering play mode.")]
        [SerializeField] private int warmupFrames;

        [Tooltip("Write every Nth frame. 1 captures every frame.")]
        [Min(1)] [SerializeField] private int captureEveryNthFrame = 1;

        [Tooltip("How many frames may be encoding at once. Each pending frame holds " +
                 "its own buffers, so this trades memory for throughput — and caps it: " +
                 "capture throttles to the encoder's rate instead of growing without bound.")]
        [Range(1, 6)] [SerializeField] private int maxPendingFrames = 2;

        [Tooltip("During capture, suppress the camera's ordinary render and put the captured " +
                 "eye on screen instead. The game view then shows exactly what is going to " +
                 "disk, and the redundant perspective pass — a third of the GPU work — is skipped. " +
                 "Turn off if the game view misbehaves or other cameras need this one enabled.")]
        [SerializeField] private bool mirrorCaptureToScreen = true;

        // ── Runtime state, read by the editor for progress display ──────────
        public bool IsCapturing { get; private set; }
        public int FrameIndex => _frameIndex;
        public int FrameLimit => _effectiveFrameLimit;
        public string ResolvedOutputPath { get; private set; }
        public Texture PreviewTexture => _preview;
        public Stereo360Projection Projection => projection;

        // Rolling mean over the last few frames. Frame cost depends on content,
        // resolution and splat count, so there is no useful a-priori estimate —
        // this only reports what the run is actually doing.
        public float AverageFrameSeconds { get; private set; }

        // Negative when unknown: no frame limit, or not enough frames measured.
        public float EstimatedSecondsRemaining =>
            _effectiveFrameLimit > 0 && AverageFrameSeconds > 0f
                ? Mathf.Max(0f, (_effectiveFrameLimit - _frameIndex) * AverageFrameSeconds)
                : -1f;

        public Vector2Int PerEyeResolution => new Vector2Int(outputWidth, outputWidth / 2);

        public Vector2Int FrameResolution
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

        // Uncompressed size. PNG on point cloud content lands around half of this,
        // but it is nearly incompressible in places, so treat it as the ceiling.
        public long RawFrameBytes
        {
            get
            {
                Vector2Int res = FrameResolution;
                return (long)res.x * res.y * (outputFormat == Stereo360OutputFormat.Exr ? 8 : 3);
            }
        }

        public Stereo360OutputFormat OutputFormat => outputFormat;

        public int EyeCount => eyeLayout == Stereo360EyeLayout.LeftEyeOnly ? 1 : 2;

        // Frames handed to the encoder but not yet on disk. Sitting at the cap
        // means encoding, not rendering, is the bottleneck.
        public int PendingEncodes => _writer?.InFlight ?? 0;
        public int PendingCapacity => _writer?.Capacity ?? maxPendingFrames;

        // Buffer memory the pool will hold while capturing, at the current settings.
        public long PendingBufferBytes
        {
            get
            {
                Vector2Int res = FrameResolution;
                int perPixel = outputFormat == Stereo360OutputFormat.Exr
                    ? 8                                       // readback only
                    : 4 + 3;                                  // readback + repack scratch
                return (long)res.x * res.y * perPixel * maxPendingFrames;
            }
        }

        private const int PreviewWidth = 256;
        private const int FrameTimeWindow = 10;

        private RenderTexture _cube;
        private RenderTexture _eyeRT;
        private RenderTexture _preview;
        private Stereo360FrameWriter _writer;
        private int _frameIndex;
        private int _effectiveFrameLimit;
        private int _prevCaptureFramerate;
        private int _tick;
        private int _warmupRemaining;
        private bool _prevCameraEnabled;
        private bool _cameraSuppressed;
        private readonly float[] _frameTimes = new float[FrameTimeWindow];
        private int _frameTimeCount;
        private float _frameStartTime;

        private void Start()
        {
            if (captureOnStart)
            {
                StartCapture();
            }
        }

        private void OnDestroy()
        {
            if (IsCapturing)
            {
                StopCapture();
            }
            ReleaseResources();
        }

        [ContextMenu("Start Capture")]
        public void StartCapture() => BeginCapture(frameLimit);

        // One frame, then stop. For checking framing and settings without
        // committing to a run that takes minutes.
        public void CaptureSingleFrame() => BeginCapture(1);

        private void BeginCapture(int limit)
        {
            if (IsCapturing)
            {
                return;
            }
            if (!Application.isPlaying)
            {
                Debug.LogError("[Stereo360Capture] Capture only works in play mode.");
                return;
            }

            if (targetCamera == null)
            {
                targetCamera = GetComponent<Camera>();
            }
            if (targetCamera == null)
            {
                targetCamera = Camera.main;
            }
            if (targetCamera == null)
            {
                Debug.LogError("[Stereo360Capture] No camera found.");
                return;
            }

            if (!ValidateOdsPrerequisites())
            {
                return;
            }

            CreateResources();
            ResolvedOutputPath = CreateTakeDirectory();

            _prevCaptureFramerate = Time.captureFramerate;
            Time.captureFramerate = frameRate;
            _frameIndex = 0;
            _effectiveFrameLimit = limit;
            _tick = 0;
            _warmupRemaining = Mathf.Max(0, warmupFrames);
            _frameTimeCount = 0;
            AverageFrameSeconds = 0f;
            _frameStartTime = Time.realtimeSinceStartup;

            // The capture drives this camera explicitly through SubmitRenderRequest
            // (and RenderToCubemap), so its automatic per-frame render is pure
            // waste — a full perspective pass over every splat that nobody reads.
            // Render requests work on a disabled camera, so switching it off costs
            // nothing and frees the screen for the mirrored capture.
            if (mirrorCaptureToScreen)
            {
                _prevCameraEnabled = targetCamera.enabled;
                targetCamera.enabled = false;
                _cameraSuppressed = true;
            }

            IsCapturing = true;
            StartCoroutine(CaptureLoop());

            Vector2Int res = FrameResolution;
            Debug.Log($"[Stereo360Capture] Capturing {res.x}x{res.y} {eyeLayout} at {frameRate} fps " +
                      $"to {ResolvedOutputPath}. " +
                      (projection == Stereo360Projection.OmniDirectionalStereo
                          ? "ODS projection: 1 render per eye, no cubemap, no per-face seams."
                          : "Cubemap projection: make sure GaussianSplatURPFeature has 'Use Render Context Matrices' enabled."));
        }

        // The splat path sizes its quads from a screen-space covariance that ODS
        // cannot produce, so it falls back to a synthesised isotropic footprint
        // that it never clamps — a splat close to the eye then rasterises a quad
        // wide enough to hang the GPU. Refuse rather than crash.
        private bool ValidateOdsPrerequisites()
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

        [ContextMenu("Stop Capture")]
        public void StopCapture()
        {
            if (!IsCapturing)
            {
                return;
            }
            IsCapturing = false;
            Time.captureFramerate = _prevCaptureFramerate;
            if (_cameraSuppressed)
            {
                targetCamera.enabled = _prevCameraEnabled;
                _cameraSuppressed = false;
            }
            Debug.Log($"[Stereo360Capture] Stopped after {_frameIndex} frames. " +
                      $"Output: {Path.GetFullPath(ResolvedOutputPath ?? outputDirectory)}");
            ReportFramesPastEnd();

            // Rendering is done but the tail of the queue is still encoding. Report
            // separately rather than blocking here — the whole point is to keep the
            // main thread free.
            if (_writer != null && _writer.InFlight > 0)
            {
                StartCoroutine(ReportDrain());
            }
        }

        private IEnumerator ReportDrain()
        {
            Stereo360FrameWriter writer = _writer;
            int queued = writer.InFlight;
            Debug.Log($"[Stereo360Capture] {queued} frame(s) still encoding in the background.");
            while (writer.InFlight > 0)
            {
                yield return null;
            }
            Debug.Log(writer.Failures > 0
                ? $"[Stereo360Capture] Encoding finished with {writer.Failures} failure(s)."
                : "[Stereo360Capture] All frames written.");
        }

        private const string DefaultFilenamePrefix = "frame";

        // Rejected by Windows in a filename, accepted by Unix. Listed by hand
        // because GetInvalidFileNameChars only reports what the running platform
        // refuses — on Linux it returns just '/' and '\0', so a capture made
        // there could otherwise produce names that no Windows tool downstream
        // can open.
        private const string NonPortableNameChars = "<>:\"|?*/\\";

        // The prefix ends up in a file path, so whatever a filesystem would reject
        // has to go before the write: that write happens on the encoder thread,
        // where an exception only ever surfaces as a Failures count, and a stray
        // separator would put frames outside the take folder entirely. Spaces and
        // control characters go with them — these names get typed into an ffmpeg
        // pattern by hand, and a space there means remembering to quote. Leading
        // and trailing dots go too: harmless on Unix, rejected by Windows.
        public static string SanitizePrefix(string prefix)
        {
            char[] platformInvalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (char c in (prefix ?? string.Empty).Trim())
            {
                bool rejected = c <= ' ' ||
                                NonPortableNameChars.IndexOf(c) >= 0 ||
                                Array.IndexOf(platformInvalid, c) >= 0;
                sb.Append(rejected ? '_' : c);
            }
            string clean = sb.ToString().Trim('.');
            return clean.Length > 0 ? clean : DefaultFilenamePrefix;
        }

        // Shared, because the writer, the leftover report and the inspector all
        // have to agree on which files belong to a run.
        public static string FileExtension(Stereo360OutputFormat format) =>
            format == Stereo360OutputFormat.Exr ? ".exr" : ".png";

        // Static so the inspector can preview a name from serialized values that
        // have not been applied to the component yet, and still go through the
        // exact path the writer uses — a preview that can disagree with what
        // lands on disk is worse than none.
        public static string FrameFileName(string prefix, int padding, int frameIndex, Stereo360OutputFormat format)
        {
            return SanitizePrefix(prefix) + "_" + frameIndex.ToString().PadLeft(padding, '0') +
                   FileExtension(format);
        }

        private string FrameFileName(int frameIndex) =>
            FrameFileName(filenamePrefix, filenamePadding, frameIndex, outputFormat);

        // Folder the next run will write to, resolved without creating anything so
        // the inspector can show it before the run commits.
        //
        // takeNumber = 0 opens a fresh folder per run. A specific number reuses
        // one and overwrites frame for frame, which is how the two passes of one
        // shot (beauty, then an AOV) stay together. Nothing is deleted, so a
        // reused take can still hold the tail of a longer run that came before —
        // StopCapture reports that instead of trimming files that may not be ours.
        public string NextTakePath
        {
            get
            {
                if (takeNumber > 0)
                {
                    return Path.Combine(outputDirectory, $"take_{takeNumber:D4}");
                }
                int take = 1;
                string path;
                do
                {
                    path = Path.Combine(outputDirectory, $"take_{take:D4}");
                    take++;
                }
                while (Directory.Exists(path));
                return path;
            }
        }

        private string CreateTakeDirectory()
        {
            Directory.CreateDirectory(outputDirectory);
            string path = NextTakePath;
            Directory.CreateDirectory(path);
            return path;
        }

        // A reused take keeps whatever an earlier, longer run left behind: frame
        // numbering restarts at zero, so those files sit past the end of this run
        // and read as a continuation of it. Nothing is deleted here — the folder
        // may hold notes or an encode that are not ours to remove — but leaving it
        // unsaid is how a spliced sequence reaches the encoder unnoticed.
        private void ReportFramesPastEnd()
        {
            if (string.IsNullOrEmpty(ResolvedOutputPath) || !Directory.Exists(ResolvedOutputPath))
            {
                return;
            }

            string prefix = SanitizePrefix(filenamePrefix) + "_";
            string ext = FileExtension(outputFormat);
            int pastEnd = 0;
            foreach (string file in Directory.GetFiles(ResolvedOutputPath, prefix + "*" + ext))
            {
                string digits = Path.GetFileNameWithoutExtension(file).Substring(prefix.Length);
                if (int.TryParse(digits, out int index) && index >= _frameIndex)
                {
                    pastEnd++;
                }
            }

            if (pastEnd > 0)
            {
                Debug.LogWarning($"[Stereo360Capture] {pastEnd} '{prefix}' frame(s) numbered past this " +
                                 $"run's {_frameIndex} remain in {ResolvedOutputPath}, left by a longer " +
                                 "take. Nothing was deleted — an encode reading the whole folder will " +
                                 "treat them as a continuation of this sequence.");
            }
        }

        private IEnumerator CaptureLoop()
        {
            var endOfFrame = new WaitForEndOfFrame();
            while (IsCapturing)
            {
                yield return endOfFrame;
                if (!IsCapturing)
                {
                    yield break;
                }

                if (_warmupRemaining > 0)
                {
                    _warmupRemaining--;
                    _frameStartTime = Time.realtimeSinceStartup;
                    continue;
                }

                if (_tick++ % Mathf.Max(1, captureEveryNthFrame) != 0)
                {
                    continue;
                }

                // Backpressure. Skipping this end-of-frame would be wrong: the
                // virtual clock advances 1/frameRate on every rendered frame
                // whether or not it was captured, so a skip punches a hole in the
                // captured timeline that shows up as a stutter in the result.
                // Block instead, which keeps capture locked to one written frame
                // per game frame.
                //
                // Readbacks are flushed first: their callbacks run on this thread,
                // and slots cannot reach the encoder — the only thing that frees
                // them — until those callbacks have fired.
                if (!_writer.HasFreeSlot)
                {
                    AsyncGPUReadback.WaitAllRequests();
                    _writer.WaitForFreeSlot();
                }

                if (!RenderFrame())
                {
                    continue;
                }
                RecordFrameTime();
                _frameIndex++;

                if (_effectiveFrameLimit > 0 && _frameIndex >= _effectiveFrameLimit)
                {
                    StopCapture();
                }
            }
        }

        private void RecordFrameTime()
        {
            float now = Time.realtimeSinceStartup;
            _frameTimes[_frameTimeCount % FrameTimeWindow] = now - _frameStartTime;
            _frameTimeCount++;
            _frameStartTime = now;

            int n = Mathf.Min(_frameTimeCount, FrameTimeWindow);
            float sum = 0f;
            for (int i = 0; i < n; i++)
            {
                sum += _frameTimes[i];
            }
            AverageFrameSeconds = sum / n;
        }

        private bool RenderFrame()
        {
            var config = GaussianSplatRenderSystem.instance.Config;
            var prevMode = config != null ? config.projectionMode : GaussianSplatProjectionMode.Perspective;
            float prevOffset = config != null ? config.odsEyeOffset : 0f;

            // Checked before a slot is taken: once readbacks are in flight they
            // hold a reference to it, so the eye loop must not be able to fail
            // partway and hand the slot back underneath them.
            if (projection == Stereo360Projection.OmniDirectionalStereo && !CanRenderOds(config))
            {
                StopCapture();
                return false;
            }

            string name = FrameFileName(_frameIndex);
            object slot = _writer.TryBeginFrame(_frameIndex, Path.Combine(ResolvedOutputPath, name), EyeCount);
            if (slot == null)
            {
                return false;
            }

            Vector2Int eyeRes = PerEyeResolution;

            for (int eye = 0; eye < EyeCount; eye++)
            {
                // LeftEyeOnly is a preview, not half a stereo pair: render it from
                // the rig centre so the framing matches what the eyes straddle.
                float offset = EyeCount == 1 ? 0f : (eye == 0 ? -0.5f : 0.5f) * eyeSeparation;

                if (projection == Stereo360Projection.OmniDirectionalStereo)
                {
                    RenderEyeOds(config, offset);
                }
                else
                {
                    RenderEyeCubemap(offset);
                }

                if (eye == 0)
                {
                    Graphics.Blit(_eyeRT, _preview);
                    if (_cameraSuppressed)
                    {
                        // WaitForEndOfFrame runs after rendering but before present,
                        // so a blit to the backbuffer here is what gets displayed:
                        // the game view shows the frame currently being written,
                        // never a stale or unrelated perspective view.
                        Graphics.Blit(_eyeRT, (RenderTexture)null);
                    }
                }

                // The readback copy is queued into the command stream here, so it
                // sees _eyeRT as it is now — the next eye can overwrite the target
                // without racing it, and the main thread never waits on the GPU.
                int capturedEye = eye;
                var layout = eyeLayout;
                // Must match _eyeRT's precision: reading an EXR capture back as
                // RGBA32 would quantise the very range EXR exists to preserve.
                TextureFormat readbackFormat = outputFormat == Stereo360OutputFormat.Exr
                    ? TextureFormat.RGBAHalf
                    : TextureFormat.RGBA32;
                AsyncGPUReadback.Request(_eyeRT, 0, readbackFormat, request =>
                {
                    if (request.hasError)
                    {
                        Debug.LogError("[Stereo360Capture] GPU readback failed; frame dropped.");
                        _writer.AbandonFrame(slot);
                        return;
                    }
                    _writer.SubmitEye(slot, request.GetData<byte>(), capturedEye,
                                      eyeRes.x, eyeRes.y, layout);
                });
            }

            if (config != null)
            {
                config.projectionMode = prevMode;
                config.odsEyeOffset = prevOffset;
            }
            return true;
        }

        private bool CanRenderOds(GaussianSplatConfig config)
        {
            if (config == null)
            {
                Debug.LogError("[Stereo360Capture] ODS capture needs an active GaussianSplatConfig in the scene.");
                return false;
            }
            if (!RenderPipeline.SupportsRenderRequest(targetCamera, new RenderPipeline.StandardRequest()))
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
        private void RenderEyeOds(GaussianSplatConfig config, float offset)
        {
            config.projectionMode = GaussianSplatProjectionMode.OmniDirectionalStereo;
            config.odsEyeOffset = offset;

            // Camera.Render() is the legacy built-in entry point: under an SRP it
            // re-enters the pipeline while this frame's render graph is still
            // executing, which strands GPU work and eventually deadlocks the next
            // GraphicsBuffer write. SubmitRenderRequest is the supported way to
            // drive an off-screen render from a coroutine.
            targetCamera.SubmitRenderRequest(new RenderPipeline.StandardRequest { destination = _eyeRT });
        }

        private void RenderEyeCubemap(float offset)
        {
            Transform camTransform = targetCamera.transform;
            Vector3 basePos = camTransform.position;
            Quaternion baseRot = camTransform.rotation;

            // Zero out the engine-side separation: it is a no-op under URP today, but
            // this capture must never double-apply an offset if that ever changes.
            float prevSeparation = targetCamera.stereoSeparation;
            targetCamera.stereoSeparation = 0f;

            for (int face = 0; face < 6; face++)
            {
                camTransform.position = basePos + baseRot * (FaceRightDir((CubemapFace)face) * offset);
                // MonoOrStereoscopicEye.Left (not Mono) so face orientation follows
                // the camera rotation; with stereoSeparation = 0 it adds no offset.
                targetCamera.RenderToCubemap(_cube, 1 << face, Camera.MonoOrStereoscopicEye.Left);
            }

            camTransform.SetPositionAndRotation(basePos, baseRot);
            targetCamera.stereoSeparation = prevSeparation;

            // Mono fills the whole 2:1 target, so each eye lands in its own image
            // and the layout step decides where it goes.
            _cube.ConvertToEquirect(_eyeRT, Camera.MonoOrStereoscopicEye.Mono);
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

        private void CreateResources()
        {
            ReleaseResources();

            Vector2Int eyeRes = PerEyeResolution;
            // An AOV carries raw values, not colour: 8 bits per channel would
            // destroy them before they ever reach the encoder.
            RenderTextureFormat eyeFormat = outputFormat == Stereo360OutputFormat.Exr
                ? RenderTextureFormat.ARGBHalf
                : RenderTextureFormat.ARGB32;
            _eyeRT = new RenderTexture(eyeRes.x, eyeRes.y, 24, eyeFormat);
            _eyeRT.Create();

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

            _preview = new RenderTexture(PreviewWidth, PreviewWidth / 2, 0, RenderTextureFormat.ARGB32);
            _preview.Create();

            Vector2Int frameRes = FrameResolution;
            _writer = new Stereo360FrameWriter(frameRes.x, frameRes.y, maxPendingFrames, outputFormat);
        }

        private void ReleaseResources()
        {
            // Readbacks reference _eyeRT and the writer's buffers, so both have to
            // be settled before anything is freed.
            AsyncGPUReadback.WaitAllRequests();
            if (_writer != null)
            {
                _writer.Dispose();
                _writer = null;
            }
            ReleaseRT(ref _cube);
            ReleaseRT(ref _eyeRT);
            ReleaseRT(ref _preview);
        }

        private static void ReleaseRT(ref RenderTexture rt)
        {
            if (rt == null)
            {
                return;
            }
            rt.Release();
            Destroy(rt);
            rt = null;
        }
    }
}
