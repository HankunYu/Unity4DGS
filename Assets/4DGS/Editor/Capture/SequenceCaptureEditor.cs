using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    // The inspector the captures share: output, timing, estimates, the take
    // that comes next, controls and progress. A subclass draws what its
    // capture adds: the view settings, the resolution and its own checks.
    public abstract class SequenceCaptureEditor : UnityEditor.Editor
    {
        private SerializedProperty m_PropTargetCamera;
        private SerializedProperty m_PropFrameRate;
        private SerializedProperty m_PropOutputDirectory;
        private SerializedProperty m_PropOutputFormat;
        private SerializedProperty m_PropTakeNumber;
        private SerializedProperty m_PropFilenamePrefix;
        private SerializedProperty m_PropFilenamePadding;
        private SerializedProperty m_PropCaptureOnStart;
        private SerializedProperty m_PropFrameLimit;
        private SerializedProperty m_PropWarmupFrames;
        private SerializedProperty m_PropCaptureEveryNthFrame;
        private SerializedProperty m_PropMaxPendingFrames;
        private SerializedProperty m_PropMirrorCaptureToScreen;

        protected virtual void OnEnable()
        {
            m_PropTargetCamera = serializedObject.FindProperty("targetCamera");
            m_PropFrameRate = serializedObject.FindProperty("frameRate");
            m_PropOutputDirectory = serializedObject.FindProperty("outputDirectory");
            m_PropOutputFormat = serializedObject.FindProperty("outputFormat");
            m_PropTakeNumber = serializedObject.FindProperty("takeNumber");
            m_PropFilenamePrefix = serializedObject.FindProperty("filenamePrefix");
            m_PropFilenamePadding = serializedObject.FindProperty("filenamePadding");
            m_PropCaptureOnStart = serializedObject.FindProperty("captureOnStart");
            m_PropFrameLimit = serializedObject.FindProperty("frameLimit");
            m_PropWarmupFrames = serializedObject.FindProperty("warmupFrames");
            m_PropCaptureEveryNthFrame = serializedObject.FindProperty("captureEveryNthFrame");
            m_PropMaxPendingFrames = serializedObject.FindProperty("maxPendingFrames");
            m_PropMirrorCaptureToScreen = serializedObject.FindProperty("mirrorCaptureToScreen");
        }

        // The section between the camera and the output, with its own heading;
        // nothing for a capture without view settings
        protected virtual void DrawViewSettings()
        {
        }

        // The resolution fields, first under the Output heading
        protected abstract void DrawResolution();

        // Read-only lines above the shared estimates
        protected virtual void DrawViewEstimates(SequenceCapture capture)
        {
        }

        // Checks of the capture's own settings
        protected virtual void DrawViewValidation(SequenceCapture capture)
        {
        }

        // Repaint continuously while a capture runs, otherwise the progress bar
        // and the preview would only update when the mouse moves over the panel.
        public override bool RequiresConstantRepaint()
        {
            var capture = target as SequenceCapture;
            return capture != null && capture.IsCapturing;
        }

        public override void OnInspectorGUI()
        {
            var capture = target as SequenceCapture;
            if (capture == null)
            {
                return;
            }

            serializedObject.Update();

            EditorGUILayout.PropertyField(m_PropTargetCamera);

            DrawViewSettings();

            EditorGUILayout.Space();
            GUILayout.Label("Output", EditorStyles.boldLabel);
            DrawResolution();
            EditorGUILayout.PropertyField(m_PropFrameRate);
            EditorGUILayout.PropertyField(m_PropOutputFormat);
            EditorGUILayout.PropertyField(m_PropOutputDirectory);
            EditorGUILayout.PropertyField(m_PropTakeNumber,
                new GUIContent(m_PropTakeNumber.intValue > 0 ? "Take Number" : "Take Number (auto)"));
            EditorGUILayout.PropertyField(m_PropFilenamePrefix);
            EditorGUILayout.PropertyField(m_PropFilenamePadding);
            // Resolved through the same call the writer uses, so a sanitised
            // prefix shows here as the name it will actually get on disk.
            EditorGUILayout.LabelField(" ",
                SequenceCapture.FrameFileName(
                    m_PropFilenamePrefix.stringValue,
                    m_PropFilenamePadding.intValue,
                    0,
                    (CaptureOutputFormat)m_PropOutputFormat.intValue),
                EditorStyles.miniLabel);

            EditorGUILayout.Space();
            GUILayout.Label("Timing", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropCaptureOnStart);
            EditorGUILayout.PropertyField(m_PropFrameLimit);
            EditorGUILayout.PropertyField(m_PropWarmupFrames);
            EditorGUILayout.PropertyField(m_PropCaptureEveryNthFrame);
            EditorGUILayout.PropertyField(m_PropMaxPendingFrames);
            EditorGUILayout.PropertyField(m_PropMirrorCaptureToScreen, new GUIContent("Mirror To Screen"));

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            DrawEstimates(capture);
            DrawValidation(capture);
            EditorGUILayout.Space();
            DrawControls(capture);
            DrawProgress(capture);
        }

        private void DrawEstimates(SequenceCapture capture)
        {
            Vector2Int frame = capture.FrameResolution;
            long rawBytes = capture.RawFrameBytes;
            int frames = m_PropFrameLimit.intValue;

            using (new EditorGUI.DisabledScope(true))
            {
                DrawViewEstimates(capture);
                EditorGUILayout.LabelField("Frame", $"{frame.x} x {frame.y}  ({capture.ViewCount} render(s)/frame)");
                EditorGUILayout.LabelField("Frame Size", $"{EditorUtility.FormatBytes(rawBytes)} raw, PNG lands well under");
                EditorGUILayout.LabelField("Total",
                    frames > 0
                        ? $"{EditorUtility.FormatBytes(rawBytes * frames)} raw over {frames} frames"
                        : "unbounded — no frame limit set");
                EditorGUILayout.LabelField("Encoder Buffers",
                    $"{EditorUtility.FormatBytes(capture.PendingBufferBytes)} resident while capturing");
            }
        }

        private void DrawValidation(SequenceCapture capture)
        {
            var cfg = GaussianSplatRenderSystem.instance.Config;
            bool aov = cfg != null && cfg.pointCloudAov != GaussianSplatAovMode.None;
            bool exr = m_PropOutputFormat.intValue == (int)CaptureOutputFormat.Exr;

            if (aov && !exr)
            {
                EditorGUILayout.HelpBox(
                    $"Config renders the {cfg.pointCloudAov} AOV but the output is PNG. Eight bits " +
                    "cannot carry a depth channel — switch the format to EXR.", MessageType.Error);
            }
            else if (!aov && exr)
            {
                EditorGUILayout.HelpBox(
                    "EXR with no AOV selected: this records the beauty pass as float, at roughly " +
                    "four times the size of the PNG equivalent.", MessageType.Warning);
            }
            if (aov && cfg.renderMode != GaussianSplatRenderMode.PointCloud)
            {
                EditorGUILayout.HelpBox(
                    $"AOV output needs renderMode = PointCloud (currently {cfg.renderMode}); " +
                    "the splat path has no nearest-sample resolve.", MessageType.Error);
            }

            DrawViewValidation(capture);

            if (!capture.IsCapturing)
            {
                string next = capture.NextTakePath;
                string prefix = SequenceCapture.SanitizePrefix(m_PropFilenamePrefix.stringValue) + "_";
                string ext = SequenceCapture.FileExtension((CaptureOutputFormat)m_PropOutputFormat.intValue);
                int existing = 0;
                try
                {
                    if (Directory.Exists(next))
                    {
                        existing = Directory.GetFiles(next, prefix + "*" + ext).Length;
                    }
                }
                catch
                {
                    // Path may be malformed while the user is typing it.
                }

                if (existing > 0)
                {
                    // Overwritten one by one, not cleared — so a run shorter than
                    // this count leaves the tail of the old sequence in place.
                    EditorGUILayout.HelpBox(
                        $"Next run writes to {next}, overwriting {existing} existing '{prefix}' frame(s) " +
                        "in place. Ones it does not reach stay behind; a different prefix or take number " +
                        "keeps the two sequences apart.",
                        MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.HelpBox($"Next run writes to {next}", MessageType.None);
                }
            }

            int frames = m_PropFrameLimit.intValue;
            if (frames > 0)
            {
                long needed = capture.RawFrameBytes * frames;
                string dir = m_PropOutputDirectory.stringValue;
                try
                {
                    string root = Path.GetPathRoot(Path.GetFullPath(string.IsNullOrEmpty(dir) ? "." : dir));
                    var drive = new DriveInfo(root);
                    if (drive.AvailableFreeSpace < needed)
                    {
                        EditorGUILayout.HelpBox(
                            $"Up to {EditorUtility.FormatBytes(needed)} of frames, but only " +
                            $"{EditorUtility.FormatBytes(drive.AvailableFreeSpace)} free.", MessageType.Warning);
                    }
                }
                catch
                {
                    // Free-space probing is best effort; never block the panel on it.
                }
            }
        }

        private void DrawControls(SequenceCapture capture)
        {
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (capture.IsCapturing)
                    {
                        if (GUILayout.Button(capture.IsDriven ? "Stop Recording" : "Stop Capture", GUILayout.Height(28)))
                        {
                            capture.StopCapture();
                        }
                    }
                    else
                    {
                        if (GUILayout.Button("Start Capture", GUILayout.Height(28)))
                        {
                            capture.StartCapture();
                        }
                        if (GUILayout.Button("Single Frame", GUILayout.Height(28), GUILayout.Width(110)))
                        {
                            capture.CaptureSingleFrame();
                        }
                    }
                }
            }

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("Capture only runs in play mode.", MessageType.None);
            }
            else if (capture.IsDriven)
            {
                EditorGUILayout.HelpBox(capture.IsTakeOpen
                        ? $"Recording the timeline's capture track: take '{capture.TakePrefix}'."
                        : "Recording the timeline's capture track: between takes.",
                    MessageType.None);
            }

            if (!string.IsNullOrEmpty(capture.ResolvedOutputPath) && GUILayout.Button("Reveal Last Take"))
            {
                EditorUtility.RevealInFinder(Path.GetFullPath(capture.ResolvedOutputPath));
            }
        }

        private void DrawProgress(SequenceCapture capture)
        {
            if (!capture.IsCapturing)
            {
                return;
            }

            EditorGUILayout.Space();
            if (!capture.IsTakeOpen)
            {
                return;
            }
            int limit = capture.FrameLimit;
            float fraction = limit > 0 ? Mathf.Clamp01((float)capture.FrameIndex / limit) : 0f;
            string label = limit > 0
                ? $"{capture.FrameIndex} / {limit}"
                : $"{capture.FrameIndex} frames — no limit, stop manually";
            Rect bar = EditorGUILayout.GetControlRect(false, 20);
            EditorGUI.ProgressBar(bar, fraction, label);

            float avg = capture.AverageFrameSeconds;
            EditorGUILayout.LabelField("Frame Time", avg > 0f ? $"{avg:F2} s" : "measuring…");

            float remaining = capture.EstimatedSecondsRemaining;
            EditorGUILayout.LabelField("Remaining",
                remaining >= 0f
                    ? System.TimeSpan.FromSeconds(remaining).ToString(@"hh\:mm\:ss")
                    : "unknown — needs a frame limit and a few measured frames");

            int pending = capture.PendingEncodes;
            EditorGUILayout.LabelField("Encoding",
                pending >= capture.PendingCapacity
                    ? $"{pending} / {capture.PendingCapacity} — encoder is the bottleneck"
                    : $"{pending} / {capture.PendingCapacity}");

            EditorGUILayout.LabelField("Take", capture.ResolvedOutputPath ?? "-");

            var preview = capture.PreviewTexture;
            if (preview != null)
            {
                Rect r = GUILayoutUtility.GetAspectRect((float)preview.width / preview.height);
                EditorGUI.DrawPreviewTexture(r, preview, null, ScaleMode.ScaleToFit);
            }
        }
    }
}
