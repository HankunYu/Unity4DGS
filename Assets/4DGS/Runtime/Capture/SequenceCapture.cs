using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // Captures a numbered image sequence at end of frame. The base of the
    // captures: it owns the session, the takes, the file names and the
    // encoder; a subclass says what one frame is — how many views it renders,
    // how each is rendered and where it goes in the output frame.
    //
    // A run is a session (the render targets, the encoder and the clock locked
    // to the frame rate) holding takes (an output folder of numbered frames).
    // Started by hand, a session holds one take whose frames are written back
    // to back. A SequenceCaptureTrack runs one session through its clips
    // instead: each clip a take, each frame numbered by its time in the clip.
    public abstract class SequenceCapture : MonoBehaviour
    {
        [Tooltip("Camera to capture from. Defaults to the camera on this GameObject, then Camera.main.")]
        [SerializeField] private Camera targetCamera;

        [Tooltip("Capture frame rate. Locks Time.captureFramerate so no frames are dropped.")]
        [SerializeField] private int frameRate = 30;

        [Tooltip("Output directory, relative to the project root. Each run writes into a " +
                 "fresh take_NNNN subfolder, so a new run can never overwrite an old one.")]
        [SerializeField] private string outputDirectory = "Recordings";

        [Tooltip("PNG for a beauty pass; EXR for any AOV. An 8-bit PNG cannot carry a " +
                 "depth channel — 256 quantisation steps leave nothing usable for compositing.")]
        [SerializeField] private CaptureOutputFormat outputFormat = CaptureOutputFormat.Png;

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

        [Tooltip("Start capturing as soon as play mode begins. Leave off when a " +
                 "SequenceCaptureTrack drives the capture.")]
        [SerializeField] private bool captureOnStart;

        [Tooltip("Stop automatically after this many frames. 0 = capture until StopCapture. " +
                 "A SequenceCaptureTrack's clips set their own length.")]
        [SerializeField] private int frameLimit;

        [Tooltip("Discard this many frames before writing anything, to let sorting and " +
                 "animation settle after entering play mode. A SequenceCaptureTrack starts " +
                 "this many frames before each clip.")]
        [SerializeField] private int warmupFrames;

        [Tooltip("Write every Nth frame. 1 captures every frame.")]
        [Min(1)] [SerializeField] private int captureEveryNthFrame = 1;

        [Tooltip("How many frames may be encoding at once. Each pending frame holds " +
                 "its own buffers, so this trades memory for throughput — and caps it: " +
                 "capture throttles to the encoder's rate instead of growing without bound.")]
        [Range(1, 6)] [SerializeField] private int maxPendingFrames = 2;

        [Tooltip("During capture, suppress the camera's ordinary render and put the captured " +
                 "view on screen instead. The game view then shows exactly what is going to " +
                 "disk, and the redundant on-screen pass is skipped. " +
                 "Turn off if the game view misbehaves or other cameras need this one enabled.")]
        [SerializeField] private bool mirrorCaptureToScreen = true;

        // ── Runtime state, read by the editor for progress display ──────────
        // A session is running
        public bool IsCapturing { get; private set; }
        // The session is a SequenceCaptureTrack's, not started by hand
        public bool IsDriven => _driver != null;
        public bool IsTakeOpen { get; private set; }
        // Frames of the open take written so far (one past the highest number)
        public int FrameIndex => _frameIndex;
        public int FrameLimit => _effectiveFrameLimit;
        public string TakePrefix => _takePrefix;
        // The prefix a manual run or an unnamed take uses
        public string TakePrefixSetting => filenamePrefix;
        public string ResolvedOutputPath { get; private set; }
        public int FrameRate => frameRate;
        public int WarmupFrames => Mathf.Max(0, warmupFrames);
        public int CaptureEveryNthFrame => Mathf.Max(1, captureEveryNthFrame);
        public Texture PreviewTexture => _preview;
        public CaptureOutputFormat OutputFormat => outputFormat;

        // Rolling mean over the last few frames. Frame cost depends on content,
        // resolution and splat count, so there is no useful a-priori estimate —
        // this only reports what the run is actually doing.
        public float AverageFrameSeconds { get; private set; }

        // Negative when unknown: no frame limit, or not enough frames measured.
        public float EstimatedSecondsRemaining =>
            _effectiveFrameLimit > 0 && AverageFrameSeconds > 0f
                ? Mathf.Max(0f, (_effectiveFrameLimit - _frameIndex) * AverageFrameSeconds)
                : -1f;

        // ── What a frame is, said by the subclass ────────────────────────────
        // The written image
        public abstract Vector2Int FrameResolution { get; }

        // Renders per frame
        public abstract int ViewCount { get; }

        // Each view's render target; views share one target, read back in turn
        protected abstract Vector2Int ViewResolution { get; }

        // Lower left corner of a view in the frame, rows counted from the bottom
        protected abstract Vector2Int ViewOffset(int view);

        protected abstract void RenderView(int view, RenderTexture target);

        // Prefix of the component's log lines, e.g. "[Stereo360Capture]"
        protected abstract string LogTag { get; }

        // Where a newly added component writes
        protected abstract string DefaultOutputDirectory { get; }

        // What a session renders, for the log line at its start
        protected abstract string SessionDescription { get; }

        // False refuses the session, after logging why
        protected virtual bool ValidatePrerequisites() => true;

        // Around the views of one frame. BeginFrame returning false ends the
        // capture; it runs before a buffer is taken, so nothing is in flight.
        protected virtual bool BeginFrame() => true;

        protected virtual void EndFrame()
        {
        }

        // Targets beyond the shared view target
        protected virtual void CreateViewResources()
        {
        }

        protected virtual void ReleaseViewResources()
        {
        }

        protected Camera TargetCamera => targetCamera;

        // Uncompressed size. PNG on point cloud content lands around half of this,
        // but it is nearly incompressible in places, so treat it as the ceiling.
        public long RawFrameBytes
        {
            get
            {
                Vector2Int res = FrameResolution;
                return (long)res.x * res.y * (outputFormat == CaptureOutputFormat.Exr ? 8 : 3);
            }
        }

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
                int perPixel = outputFormat == CaptureOutputFormat.Exr
                    ? 8                                       // readback only
                    : 4 + 3;                                  // readback + repack scratch
                return (long)res.x * res.y * perPixel * maxPendingFrames;
            }
        }

        private const int PreviewWidth = 256;
        private const int FrameTimeWindow = 10;

        private RenderTexture _viewRT;
        private RenderTexture _preview;
        private CaptureFrameWriter _writer;
        private int _frameIndex;
        private int _effectiveFrameLimit;
        private string _takePrefix = DefaultFilenamePrefix;
        // The session's driver and its end-of-frame call, and the frame number
        // it asked this frame to be written as (-1 for none)
        private object _driver;
        private Action _driverEndOfFrame;
        private int _requestedFrame = -1;
        private int _prevCaptureFramerate;
        private int _tick;
        private int _warmupRemaining;
        private bool _prevCameraEnabled;
        private bool _cameraSuppressed;
        private readonly float[] _frameTimes = new float[FrameTimeWindow];
        private int _frameTimeCount;
        private float _frameStartTime;

        private void Reset()
        {
            outputDirectory = DefaultOutputDirectory;
        }

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
            if (!BeginSession(null, null))
            {
                return;
            }
            BeginTake(filenamePrefix, limit);
            _warmupRemaining = WarmupFrames;
        }

        // Starts a session for a driver (a SequenceCaptureTrack, or null when
        // started by hand): the resources, and the clock locked to the frame
        // rate so every frame advances time by exactly one frame. A driver
        // opens takes, names the frames to write (WriteFrame) and gets
        // endOfFrame after each frame. False when a session already runs or
        // the capture cannot start.
        internal bool BeginSession(object driver, Action endOfFrame)
        {
            if (IsCapturing)
            {
                return false;
            }
            if (!Application.isPlaying)
            {
                Debug.LogError($"{LogTag} Capture only works in play mode.");
                return false;
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
                Debug.LogError($"{LogTag} No camera found.");
                return false;
            }

            if (!ValidatePrerequisites())
            {
                return false;
            }

            CreateResources();

            _prevCaptureFramerate = Time.captureFramerate;
            Time.captureFramerate = frameRate;
            _driver = driver;
            _driverEndOfFrame = endOfFrame;
            _requestedFrame = -1;
            _warmupRemaining = 0;

            // The capture drives this camera explicitly through render requests,
            // so its automatic per-frame render is pure waste — a full pass over
            // every splat that nobody reads. Render requests work on a disabled
            // camera, so switching it off costs nothing and frees the screen for
            // the mirrored capture.
            if (mirrorCaptureToScreen)
            {
                _prevCameraEnabled = targetCamera.enabled;
                targetCamera.enabled = false;
                _cameraSuppressed = true;
            }

            IsCapturing = true;
            StartCoroutine(CaptureLoop());

            Vector2Int res = FrameResolution;
            Debug.Log($"{LogTag} Capturing {res.x}x{res.y} at {frameRate} fps" +
                      (driver != null ? ", driven by the timeline. " : ". ") + SessionDescription);
            return true;
        }

        // Opens a take: a take folder (NextTakePath) of frames named
        // <prefix>_<number>, up to limit frames (0 = no limit)
        internal void BeginTake(string prefix, int limit)
        {
            if (!IsCapturing)
            {
                return;
            }
            EndTake();
            ResolvedOutputPath = CreateTakeDirectory();
            _takePrefix = SanitizePrefix(string.IsNullOrEmpty(prefix) ? filenamePrefix : prefix);
            _frameIndex = 0;
            _effectiveFrameLimit = limit;
            _tick = 0;
            _frameTimeCount = 0;
            AverageFrameSeconds = 0f;
            _frameStartTime = Time.realtimeSinceStartup;
            IsTakeOpen = true;
            Debug.Log($"{LogTag} Take '{_takePrefix}' to {ResolvedOutputPath}" +
                      (limit > 0 ? $", {limit} frames." : "."));
        }

        internal void EndTake()
        {
            if (!IsTakeOpen)
            {
                return;
            }
            IsTakeOpen = false;
            Debug.Log($"{LogTag} Take '{_takePrefix}' ended after {_frameIndex} frames. " +
                      $"Output: {Path.GetFullPath(ResolvedOutputPath ?? outputDirectory)}");
            ReportFramesPastEnd();
        }

        // Writes this frame, at its end, as frame `index` of the open take
        internal void WriteFrame(int index)
        {
            if (IsTakeOpen && index >= 0)
            {
                _requestedFrame = index;
            }
        }

        // Ends the session, whoever started it, and its open take
        [ContextMenu("Stop Capture")]
        public void StopCapture()
        {
            if (!IsCapturing)
            {
                return;
            }
            EndTake();
            IsCapturing = false;
            _driver = null;
            _driverEndOfFrame = null;
            _requestedFrame = -1;
            Time.captureFramerate = _prevCaptureFramerate;
            if (_cameraSuppressed)
            {
                targetCamera.enabled = _prevCameraEnabled;
                _cameraSuppressed = false;
            }

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
            CaptureFrameWriter writer = _writer;
            int queued = writer.InFlight;
            Debug.Log($"{LogTag} {queued} frame(s) still encoding in the background.");
            while (writer.InFlight > 0)
            {
                yield return null;
            }
            Debug.Log(writer.Failures > 0
                ? $"{LogTag} Encoding finished with {writer.Failures} failure(s)."
                : $"{LogTag} All frames written.");
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
        public static string FileExtension(CaptureOutputFormat format) =>
            format == CaptureOutputFormat.Exr ? ".exr" : ".png";

        // Static so the inspector can preview a name from serialized values that
        // have not been applied to the component yet, and still go through the
        // exact path the writer uses — a preview that can disagree with what
        // lands on disk is worse than none.
        public static string FrameFileName(string prefix, int padding, int frameIndex, CaptureOutputFormat format)
        {
            return SanitizePrefix(prefix) + "_" + frameIndex.ToString().PadLeft(padding, '0') +
                   FileExtension(format);
        }

        private string FrameFileName(int frameIndex) =>
            FrameFileName(_takePrefix, filenamePadding, frameIndex, outputFormat);

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

            string prefix = _takePrefix + "_";
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
                Debug.LogWarning($"{LogTag} {pastEnd} '{prefix}' frame(s) numbered past this " +
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

                if (_driver != null)
                {
                    int requested = _requestedFrame;
                    _requestedFrame = -1;
                    if (requested >= 0 && IsTakeOpen)
                    {
                        WriteTakeFrame(requested);
                    }
                    else
                    {
                        _frameStartTime = Time.realtimeSinceStartup;
                    }
                    _driverEndOfFrame?.Invoke();
                    continue;
                }

                if (_warmupRemaining > 0)
                {
                    _warmupRemaining--;
                    _frameStartTime = Time.realtimeSinceStartup;
                    continue;
                }

                if (_tick++ % CaptureEveryNthFrame != 0)
                {
                    continue;
                }

                if (!WriteTakeFrame(_frameIndex))
                {
                    continue;
                }

                if (_effectiveFrameLimit > 0 && _frameIndex >= _effectiveFrameLimit)
                {
                    StopCapture();
                }
            }
        }

        private bool WriteTakeFrame(int index)
        {
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

            if (!RenderFrame(index))
            {
                return false;
            }
            RecordFrameTime();
            _frameIndex = Mathf.Max(_frameIndex, index + 1);
            return true;
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

        private bool RenderFrame(int frameIndex)
        {
            // Checked before a slot is taken: once readbacks are in flight they
            // hold a reference to it, so the view loop must not be able to fail
            // partway and hand the slot back underneath them.
            if (!BeginFrame())
            {
                StopCapture();
                return false;
            }

            string name = FrameFileName(frameIndex);
            int viewCount = ViewCount;
            object slot = _writer.TryBeginFrame(frameIndex, Path.Combine(ResolvedOutputPath, name), viewCount);
            if (slot == null)
            {
                EndFrame();
                return false;
            }

            Vector2Int viewRes = ViewResolution;

            for (int view = 0; view < viewCount; view++)
            {
                RenderView(view, _viewRT);

                if (view == 0)
                {
                    Graphics.Blit(_viewRT, _preview);
                    if (_cameraSuppressed)
                    {
                        // WaitForEndOfFrame runs after rendering but before present,
                        // so a blit to the backbuffer here is what gets displayed:
                        // the game view shows the frame currently being written,
                        // never a stale or unrelated view.
                        Graphics.Blit(_viewRT, (RenderTexture)null);
                    }
                }

                // The readback copy is queued into the command stream here, so it
                // sees the target as it is now — the next view can overwrite it
                // without racing it, and the main thread never waits on the GPU.
                Vector2Int offset = ViewOffset(view);
                // Must match the target's precision: reading an EXR capture back as
                // RGBA32 would quantise the very range EXR exists to preserve.
                TextureFormat readbackFormat = outputFormat == CaptureOutputFormat.Exr
                    ? TextureFormat.RGBAHalf
                    : TextureFormat.RGBA32;
                AsyncGPUReadback.Request(_viewRT, 0, readbackFormat, request =>
                {
                    if (request.hasError)
                    {
                        Debug.LogError($"{LogTag} GPU readback failed; frame dropped.");
                        _writer.AbandonFrame(slot);
                        return;
                    }
                    _writer.SubmitView(slot, request.GetData<byte>(), viewRes.x, viewRes.y, offset.x, offset.y);
                });
            }

            EndFrame();
            return true;
        }

        private void CreateResources()
        {
            ReleaseResources();

            Vector2Int viewRes = ViewResolution;
            // An AOV carries raw values, not colour: 8 bits per channel would
            // destroy them before they ever reach the encoder.
            RenderTextureFormat viewFormat = outputFormat == CaptureOutputFormat.Exr
                ? RenderTextureFormat.ARGBHalf
                : RenderTextureFormat.ARGB32;
            _viewRT = new RenderTexture(viewRes.x, viewRes.y, 24, viewFormat);
            _viewRT.Create();

            CreateViewResources();

            _preview = new RenderTexture(PreviewWidth, Mathf.Max(1, PreviewWidth * viewRes.y / viewRes.x), 0,
                RenderTextureFormat.ARGB32);
            _preview.Create();

            Vector2Int frameRes = FrameResolution;
            _writer = new CaptureFrameWriter(frameRes.x, frameRes.y, maxPendingFrames, outputFormat);
        }

        private void ReleaseResources()
        {
            // Readbacks reference the view target and the writer's buffers, so
            // both have to be settled before anything is freed.
            AsyncGPUReadback.WaitAllRequests();
            if (_writer != null)
            {
                _writer.Dispose();
                _writer = null;
            }
            ReleaseViewResources();
            ReleaseRT(ref _viewRT);
            ReleaseRT(ref _preview);
        }

        protected static void ReleaseRT(ref RenderTexture rt)
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
