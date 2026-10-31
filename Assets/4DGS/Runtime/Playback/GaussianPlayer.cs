using UnityEngine;

namespace GaussianSplatting.Runtime
{
    // Plays a GaussianSplatSequence on the renderer next to it, one asset per
    // frame. The renderer's buffers are sized once for the largest frame, so
    // switching frames only uploads data.
    [AddComponentMenu("Gaussian Splatting/Player")]
    [RequireComponent(typeof(GaussianSplatRenderer))]
    public class GaussianPlayer : MonoBehaviour
    {
        public GaussianSplatSequence sequence;

        // Multiplies the sequence's frame rate
        [Min(0f)] public float playbackSpeed = 1f;

        public int startFrame = 0;
        // -1 plays to the last frame
        public int endFrame = -1;

        public bool autoPlay = true;
        public bool loop = true;
        public bool isPlaying = true;

        // The frame on screen
        public int currentFrame = 0;

        private GaussianSplatRenderer _gsRender;
        private float _timer = 0f;

        private void Start()
        {
            Init();
        }

        public void Init()
        {
            _gsRender = GetComponent<GaussianSplatRenderer>();
            _timer = 0f;

            if (sequence == null || sequence.FrameCount == 0)
            {
                Debug.LogError($"[GaussianPlayer] {name}: no sequence, or it has no frames", this);
                enabled = false;
                return;
            }

            long posDataMaxSize = 0;
            long otherDataMaxSize = 0;
            long shDataMaxSize = 0;
            long chunkDataMaxSize = 0;
            long splatCountMaxSize = 0;
            foreach (var asset in sequence.frames)
            {
                if (asset == null)
                {
                    Debug.LogError($"[GaussianPlayer] {name}: sequence {sequence.name} has a missing frame", this);
                    enabled = false;
                    return;
                }
                posDataMaxSize = asset.posData.dataSize > posDataMaxSize ? asset.posData.dataSize : posDataMaxSize;
                otherDataMaxSize = asset.otherData.dataSize > otherDataMaxSize ? asset.otherData.dataSize : otherDataMaxSize;
                shDataMaxSize = asset.SHDataSize > shDataMaxSize ? asset.SHDataSize : shDataMaxSize;
                if (asset.chunkData != null && asset.chunkData.dataSize != 0)
                    chunkDataMaxSize = asset.chunkData.dataSize > chunkDataMaxSize ? asset.chunkData.dataSize : chunkDataMaxSize;
                splatCountMaxSize = asset.splatCount > splatCountMaxSize ? asset.splatCount : splatCountMaxSize;
            }

            int lastFrame = sequence.FrameCount - 1;
            startFrame = Mathf.Clamp(startFrame, 0, lastFrame);
            if (endFrame < startFrame || endFrame > lastFrame)
                endFrame = lastFrame;
            currentFrame = startFrame;

            // Assign the first frame before buffer init so the renderer never
            // sizes resources from an unrelated inspector-assigned asset.
            _gsRender.splatAsset = sequence.frames[startFrame];
            _gsRender.nextAsset = sequence.frames[startFrame];
            _gsRender.InitResourcesForAssets(posDataMaxSize, otherDataMaxSize, shDataMaxSize, chunkDataMaxSize, splatCountMaxSize);

            isPlaying = autoPlay;
        }

        public void Play()
        {
            isPlaying = true;
        }

        public void Stop()
        {
            isPlaying = false;
            _timer = 0f;
            ShowFrame(startFrame);
        }

        public void Pause()
        {
            isPlaying = false;
        }

        public void Resume()
        {
            Play();
        }

        private void Update()
        {
            Advance(Time.deltaTime);
        }

        // Moves on by however many frames deltaTime covers, so playback keeps
        // its speed when rendering runs slower than the sequence
        internal void Advance(float deltaTime)
        {
            if (!isPlaying || _gsRender == null || sequence == null || endFrame <= startFrame)
                return;

            _timer += deltaTime * playbackSpeed;
            float interval = 1f / sequence.frameRate;
            int steps = (int)(_timer / interval);
            if (steps == 0)
                return;
            _timer -= steps * interval;

            int frame = currentFrame + steps;
            if (frame > endFrame)
            {
                if (loop)
                {
                    frame = startFrame + (frame - startFrame) % (endFrame - startFrame + 1);
                }
                else
                {
                    frame = endFrame;
                    isPlaying = false;
                }
            }
            ShowFrame(frame);
        }

        internal void ShowFrame(int frame)
        {
            currentFrame = frame;
            if (_gsRender != null && sequence != null && frame < sequence.FrameCount)
                _gsRender.nextAsset = sequence.frames[frame];
        }
    }
}
