using System.IO;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(Stereo360Capture))]
    public class Stereo360CaptureEditor : UnityEditor.Editor
    {
        private SerializedProperty m_PropTargetCamera;
        private SerializedProperty m_PropProjection;
        private SerializedProperty m_PropEyeLayout;
        private SerializedProperty m_PropEyeSeparation;
        private SerializedProperty m_PropCubemapFaceSize;
        private SerializedProperty m_PropOutputWidth;
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

        private static readonly int[] WidthPresets = { 2048, 4096, 5760, 8192 };
        private static readonly string[] WidthPresetLabels = { "2K (2048)", "4K (4096)", "6K (5760)", "8K (8192)", "Custom" };

        private void OnEnable()
        {
            m_PropTargetCamera = serializedObject.FindProperty("targetCamera");
            m_PropProjection = serializedObject.FindProperty("projection");
            m_PropEyeLayout = serializedObject.FindProperty("eyeLayout");
            m_PropEyeSeparation = serializedObject.FindProperty("eyeSeparation");
            m_PropCubemapFaceSize = serializedObject.FindProperty("cubemapFaceSize");
            m_PropOutputWidth = serializedObject.FindProperty("outputWidth");
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

        // Repaint continuously while a capture runs, otherwise the progress bar
        // and the preview would only update when the mouse moves over the panel.
        public override bool RequiresConstantRepaint()
        {
            var capture = target as Stereo360Capture;
            return capture != null && capture.IsCapturing;
        }

        public override void OnInspectorGUI()
        {
            var capture = target as Stereo360Capture;
            if (capture == null)
            {
                return;
            }

            serializedObject.Update();

            EditorGUILayout.PropertyField(m_PropTargetCamera);

            EditorGUILayout.Space();
            GUILayout.Label("Projection", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(m_PropProjection);
            EditorGUILayout.PropertyField(m_PropEyeLayout);
            if (m_PropEyeLayout.intValue != (int)Stereo360EyeLayout.LeftEyeOnly)
            {
                EditorGUILayout.PropertyField(m_PropEyeSeparation);
            }
            if (m_PropProjection.intValue == (int)Stereo360Projection.Cubemap)
            {
                EditorGUILayout.PropertyField(m_PropCubemapFaceSize);
            }

            EditorGUILayout.Space();
            GUILayout.Label("Output", EditorStyles.boldLabel);
            DrawWidthPreset();
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
                Stereo360Capture.FrameFileName(
                    m_PropFilenamePrefix.stringValue,
                    m_PropFilenamePadding.intValue,
                    0,
                    (Stereo360OutputFormat)m_PropOutputFormat.intValue),
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

        private void DrawWidthPreset()
        {
            int current = m_PropOutputWidth.intValue;
            int index = System.Array.IndexOf(WidthPresets, current);
            if (index < 0)
            {
                index = WidthPresets.Length;
            }

            int picked = EditorGUILayout.Popup("Output Width", index, WidthPresetLabels);
            if (picked != index && picked < WidthPresets.Length)
            {
                m_PropOutputWidth.intValue = WidthPresets[picked];
            }
            if (picked == WidthPresets.Length)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.PropertyField(m_PropOutputWidth, new GUIContent("Custom Width"));
                EditorGUI.indentLevel--;
            }
        }

        private void DrawEstimates(Stereo360Capture capture)
        {
            Vector2Int eye = capture.PerEyeResolution;
            Vector2Int frame = capture.FrameResolution;
            long rawBytes = capture.RawFrameBytes;
            int frames = m_PropFrameLimit.intValue;

            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.LabelField("Per Eye", $"{eye.x} x {eye.y}");
                EditorGUILayout.LabelField("Frame", $"{frame.x} x {frame.y}  ({capture.EyeCount} render(s)/frame)");
                EditorGUILayout.LabelField("Frame Size", $"{EditorUtility.FormatBytes(rawBytes)} raw, PNG lands well under");
                EditorGUILayout.LabelField("Total",
                    frames > 0
                        ? $"{EditorUtility.FormatBytes(rawBytes * frames)} raw over {frames} frames"
                        : "unbounded — no frame limit set");
                EditorGUILayout.LabelField("Encoder Buffers",
                    $"{EditorUtility.FormatBytes(capture.PendingBufferBytes)} resident while capturing");
            }
        }

        private void DrawValidation(Stereo360Capture capture)
        {
            var cfg = GaussianSplatRenderSystem.instance.Config;
            bool aov = cfg != null && cfg.pointCloudAov != GaussianSplatAovMode.None;
            bool exr = m_PropOutputFormat.intValue == (int)Stereo360OutputFormat.Exr;

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

            if (m_PropProjection.intValue == (int)Stereo360Projection.OmniDirectionalStereo)
            {
                var config = GaussianSplatRenderSystem.instance.Config;
                if (config == null)
                {
                    EditorGUILayout.HelpBox(
                        "ODS needs an active GaussianSplatConfig in the scene.", MessageType.Error);
                }
                else if (config.renderMode != GaussianSplatRenderMode.PointCloud)
                {
                    EditorGUILayout.HelpBox(
                        $"ODS requires renderMode = PointCloud (currently {config.renderMode}). The splat path " +
                        "never clamps the synthesised footprint, so a splat near the eye can hang the GPU.",
                        MessageType.Error);
                }
            }
            else
            {
                // Angular density match: an equirect of width W needs W/(2*pi) px
                // per radian, and a cube face of size N delivers N/2 px per radian
                // at its centre, so N = W/pi is the break-even point.
                float ideal = m_PropOutputWidth.intValue / Mathf.PI;
                int face = m_PropCubemapFaceSize.intValue;
                if (face < ideal * 0.9f)
                {
                    EditorGUILayout.HelpBox(
                        $"Cubemap face {face} undersamples the equirect (match is ~{Mathf.RoundToInt(ideal)}). " +
                        "The panorama will be soft.", MessageType.Warning);
                }
                else if (face > ideal * 1.5f)
                {
                    EditorGUILayout.HelpBox(
                        $"Cubemap face {face} is far above the match (~{Mathf.RoundToInt(ideal)}). " +
                        "The cube RT has no mip chain, so that minification aliases instead of filtering.",
                        MessageType.Warning);
                }

                EditorGUILayout.HelpBox(
                    "Cubemap projection applies a constant eye offset per face, so the eyes disagree along " +
                    "the 12 cube edges and at the 8 corners. Use ODS unless you need non-splat geometry.",
                    MessageType.Info);
            }

            if (!capture.IsCapturing)
            {
                string next = capture.NextTakePath;
                string prefix = Stereo360Capture.SanitizePrefix(m_PropFilenamePrefix.stringValue) + "_";
                string ext = Stereo360Capture.FileExtension((Stereo360OutputFormat)m_PropOutputFormat.intValue);
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

        private void DrawControls(Stereo360Capture capture)
        {
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (capture.IsCapturing)
                    {
                        if (GUILayout.Button("Stop Capture", GUILayout.Height(28)))
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

            if (!string.IsNullOrEmpty(capture.ResolvedOutputPath) && GUILayout.Button("Reveal Last Take"))
            {
                EditorUtility.RevealInFinder(Path.GetFullPath(capture.ResolvedOutputPath));
            }
        }

        private void DrawProgress(Stereo360Capture capture)
        {
            if (!capture.IsCapturing)
            {
                return;
            }

            EditorGUILayout.Space();
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
                Rect r = GUILayoutUtility.GetAspectRect(2f);
                EditorGUI.DrawPreviewTexture(r, preview, null, ScaleMode.ScaleToFit);
            }
        }
    }
}
