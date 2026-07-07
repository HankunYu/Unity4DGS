using UnityEngine;
using GaussianSplatting.Runtime;

[RequireComponent(typeof(GaussianSplatRenderer))]
public class GaussianPlayer : MonoBehaviour
{
    public string resourceFolder = "";

    public float frameRate = 22f;

    public int startFrame = 0;
    public int endFrame = -1;

    public bool autoPlay = true;
    public bool loop = true;
    public bool isPlaying = true;

    public GaussianSplatAsset[] assetList;
    private GaussianSplatRenderer _gsRender;
    public int currentFrame = 0;
    private float _timer = 0f;
    private float _frameInterval;

    private void Start()
    {
        Init();
    }
    public void Init()
    {
        _gsRender = GetComponent<GaussianSplatRenderer>();
        _frameInterval = 1f / frameRate;

        if (resourceFolder == "")
        {
            Debug.LogError("Please set resourceFolder for Unity4DGS.");
            enabled = false;
            return;
        }

        // load assets
        assetList = Resources.LoadAll<GaussianSplatAsset>(resourceFolder);
        Debug.Log($"[GaussianPlayer] Loaded {assetList.Length} frames from Resources/{resourceFolder}");
        if (assetList.Length == 0)
        {
            Debug.LogError("No frames found!");
            enabled = false;
            return;
        }

        long posDataMaxSize = 0;
        long otherDataMaxSize = 0;
        long shDataMaxSize = 0;
        long chunkDataMaxSize = 0;
        long splatCountMaxSize = 0;
        foreach (var asset in assetList)
        {
            posDataMaxSize = asset.posData.dataSize > posDataMaxSize ? asset.posData.dataSize : posDataMaxSize;
            otherDataMaxSize = asset.otherData.dataSize > otherDataMaxSize ? asset.otherData.dataSize : otherDataMaxSize;
            shDataMaxSize = asset.shData.dataSize > shDataMaxSize ? asset.shData.dataSize : shDataMaxSize;
            if (asset.chunkData != null && asset.chunkData.dataSize != 0)
                chunkDataMaxSize = asset.chunkData.dataSize > chunkDataMaxSize ? asset.chunkData.dataSize : chunkDataMaxSize;
            splatCountMaxSize = asset.splatCount > splatCountMaxSize ? asset.splatCount : splatCountMaxSize;
        }
        Debug.Log($"[GaussianPlayer] Max buffer sizes — pos:{posDataMaxSize} other:{otherDataMaxSize} sh:{shDataMaxSize} chunk:{chunkDataMaxSize} splats:{splatCountMaxSize}");

        startFrame = Mathf.Clamp(startFrame, 0, assetList.Length - 1);
        if (endFrame < startFrame || endFrame >= assetList.Length)
            endFrame = assetList.Length - 1;
        currentFrame = startFrame;

        // Assign the first frame before buffer init so the renderer never
        // sizes resources from an unrelated inspector-assigned asset.
        _gsRender.splatAsset = assetList[startFrame];
        _gsRender.nextAsset = assetList[startFrame];
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
        currentFrame = startFrame;
        _timer = 0f;
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
        if (isPlaying && assetList != null && assetList.Length > 0)
        {
            if (endFrame <= startFrame) return;
            _timer += Time.deltaTime;
            if (_timer >= _frameInterval)
            {
                _timer -= _frameInterval;
                NextFrame();
            }
        }
    }

    private void NextFrame()
    {
        _gsRender.nextAsset = assetList[currentFrame];

        currentFrame++;

        if (currentFrame > endFrame)
        {
            if (loop)
                currentFrame = startFrame;
            else
            {
                currentFrame = endFrame;
                isPlaying = false;
            }
        }
    }
}
