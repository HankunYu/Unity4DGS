using System.Collections;
using System.IO;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Captures a stereo over-under 360 equirect PNG sequence at end of frame.
    //
    // Unity Recorder's stereo 360 path is unsupported on SRP: Camera.stereoSeparation
    // is only honoured by the built-in pipeline's RenderToCubemap eye offset, so both
    // eyes come out pixel-identical under URP. This component replicates the built-in
    // behaviour in user code: each cubemap face is rendered with the camera displaced
    // by half the eye separation along that face's horizontal right axis, per eye.
    //
    // Requires the GaussianSplatURPFeature "Use Render Context Matrices" toggle so
    // splats re-sort for every one of the 12 face renders per frame.
    public class Stereo360Capture : MonoBehaviour
    {
        [Tooltip("Camera to capture from. Defaults to the camera on this GameObject, then Camera.main.")]
        [SerializeField] private Camera targetCamera;

        [Tooltip("Interpupillary distance in meters.")]
        [SerializeField] private float eyeSeparation = 0.065f;

        [Tooltip("Resolution of each cubemap face. ~outputWidth/4 minimum; higher oversamples.")]
        [SerializeField] private int cubemapFaceSize = 2048;

        [Tooltip("Output width in pixels. Per-eye equirect is width x width/2; over-under total is width x width.")]
        [SerializeField] private int outputWidth = 5760;

        [Tooltip("Capture frame rate. Locks Time.captureFramerate so no frames are dropped.")]
        [SerializeField] private int frameRate = 30;

        [Tooltip("PNG output directory, relative to the project root.")]
        [SerializeField] private string outputDirectory = "Recordings/Stereo360";

        [Tooltip("Start capturing as soon as play mode begins.")]
        [SerializeField] private bool captureOnStart;

        [Tooltip("Stop automatically after this many frames. 0 = capture until StopCapture.")]
        [SerializeField] private int frameLimit;

        public bool IsCapturing { get; private set; }

        private RenderTexture _leftCube;
        private RenderTexture _rightCube;
        private RenderTexture _equirect;
        private Texture2D _readbackTex;
        private int _frameIndex;
        private int _prevCaptureFramerate;

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
        public void StartCapture()
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

            CreateResources();
            Directory.CreateDirectory(outputDirectory);

            _prevCaptureFramerate = Time.captureFramerate;
            Time.captureFramerate = frameRate;
            _frameIndex = 0;
            IsCapturing = true;
            StartCoroutine(CaptureLoop());

            Debug.Log($"[Stereo360Capture] Capturing {outputWidth}x{outputWidth} over-under at {frameRate} fps to {outputDirectory}. " +
                      "Make sure GaussianSplatURPFeature has 'Use Render Context Matrices' enabled.");
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
            Debug.Log($"[Stereo360Capture] Stopped after {_frameIndex} frames. Output: {Path.GetFullPath(outputDirectory)}");
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

                RenderStereoFrame();
                SaveFrame();
                _frameIndex++;

                if (_frameIndex % 30 == 0)
                {
                    Debug.Log($"[Stereo360Capture] {_frameIndex} frames captured.");
                }
                if (frameLimit > 0 && _frameIndex >= frameLimit)
                {
                    StopCapture();
                }
            }
        }

        private void RenderStereoFrame()
        {
            Transform camTransform = targetCamera.transform;
            Vector3 basePos = camTransform.position;
            Quaternion baseRot = camTransform.rotation;

            // Zero out the engine-side separation: it is a no-op under URP today, but
            // this capture must never double-apply an offset if that ever changes.
            float prevSeparation = targetCamera.stereoSeparation;
            targetCamera.stereoSeparation = 0f;

            for (int eye = 0; eye < 2; eye++)
            {
                RenderTexture cube = eye == 0 ? _leftCube : _rightCube;
                float offset = (eye == 0 ? -0.5f : 0.5f) * eyeSeparation;
                for (int face = 0; face < 6; face++)
                {
                    camTransform.position = basePos + baseRot * (FaceRightDir((CubemapFace)face) * offset);
                    // MonoOrStereoscopicEye.Left (not Mono) so face orientation follows
                    // the camera rotation; with stereoSeparation = 0 it adds no offset.
                    targetCamera.RenderToCubemap(cube, 1 << face, Camera.MonoOrStereoscopicEye.Left);
                }
            }

            camTransform.SetPositionAndRotation(basePos, baseRot);
            targetCamera.stereoSeparation = prevSeparation;

            _leftCube.ConvertToEquirect(_equirect, Camera.MonoOrStereoscopicEye.Left);
            _rightCube.ConvertToEquirect(_equirect, Camera.MonoOrStereoscopicEye.Right);
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

        private void SaveFrame()
        {
            RenderTexture prevActive = RenderTexture.active;
            RenderTexture.active = _equirect;
            _readbackTex.ReadPixels(new Rect(0, 0, _equirect.width, _equirect.height), 0, 0);
            _readbackTex.Apply(false);
            RenderTexture.active = prevActive;

            string path = Path.Combine(outputDirectory, $"frame_{_frameIndex:D4}.png");
            File.WriteAllBytes(path, _readbackTex.EncodeToPNG());
        }

        private void CreateResources()
        {
            ReleaseResources();

            _leftCube = CreateCubeRT();
            _rightCube = CreateCubeRT();
            _equirect = new RenderTexture(outputWidth, outputWidth, 0, RenderTextureFormat.ARGB32);
            _equirect.Create();
            _readbackTex = new Texture2D(outputWidth, outputWidth, TextureFormat.RGB24, false);
        }

        private RenderTexture CreateCubeRT()
        {
            var rt = new RenderTexture(cubemapFaceSize, cubemapFaceSize, 24, RenderTextureFormat.ARGB32)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Cube
            };
            rt.Create();
            return rt;
        }

        private void ReleaseResources()
        {
            if (_leftCube != null)
            {
                _leftCube.Release();
                Destroy(_leftCube);
                _leftCube = null;
            }
            if (_rightCube != null)
            {
                _rightCube.Release();
                Destroy(_rightCube);
                _rightCube = null;
            }
            if (_equirect != null)
            {
                _equirect.Release();
                Destroy(_equirect);
                _equirect = null;
            }
            if (_readbackTex != null)
            {
                Destroy(_readbackTex);
                _readbackTex = null;
            }
        }
    }
}
