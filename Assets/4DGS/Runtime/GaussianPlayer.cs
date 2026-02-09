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
    private GaussianSplatRenderer gsRender;
    public int currentFrame = 0;
    private float timer = 0f;
    private float frameInterval;

    void Start()
    {
        Init();
    }
    public void Init()
    {
        gsRender = GetComponent<GaussianSplatRenderer>();
        frameInterval = 1f / frameRate;

        // load assets
        if (resourceFolder != "")
        {
            assetList = Resources.LoadAll<GaussianSplatAsset>(resourceFolder);
            Debug.Log($"Loaded {assetList.Length} frames from Resources/{resourceFolder}");
            if (assetList.Length == 0)
            {
                Debug.LogError("No frames found!");
                enabled = false;
                return;
            }
            else
            {
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
                    splatCountMaxSize = asset.splatCount > splatCountMaxSize ? asset.splatCount : posDataMaxSize;
                }
                Debug.Log($"posDataMaxSize: {posDataMaxSize}\n" +
                    $"otherDataMaxSize: {otherDataMaxSize}\n" +
                    $"shDataMaxSize: {shDataMaxSize}\n" +
                    $"chunkDataMaxSize: {chunkDataMaxSize}\n" +
                    $"splatCountMaxSize: {splatCountMaxSize}");
                gsRender.InitResourcesForAssets(posDataMaxSize, otherDataMaxSize, shDataMaxSize, chunkDataMaxSize, splatCountMaxSize);
            }
            if (endFrame < 0 || endFrame >= assetList.Length)
                endFrame = assetList.Length - 1;

            if (autoPlay)
                Play();

            if (assetList.Length > 0)
            {
                gsRender.m_Asset = assetList[0];
                gsRender.m_NextAsset = assetList[0];
            }
        }
        else
        {
            Debug.LogError("Please set resourceFolder for 4dgs.");
            return;
        }
    }

    public void Play()
    {
        isPlaying = true;
    }
    public void Stop()
    {
        isPlaying = false;
        currentFrame = startFrame;
    }

    public void Pause()
    {
        isPlaying = false;
    }

    public void Resume()
    {
        Play();
    }

    void Update()
    {
        if (isPlaying && assetList.Length>0)
        {
            if (endFrame == 0) return;
            timer += Time.deltaTime;
            if (timer >= frameInterval)
            {
                timer -= frameInterval;
                NextFrame();
            }
        }
    }

    private void NextFrame()
    {
        //gsRender.m_Asset = assetList[currentFrame];
        gsRender.m_NextAsset = assetList[currentFrame];

        currentFrame++;

        if (currentFrame > endFrame)
        {
            if (loop)
                currentFrame = startFrame;
            else
            {
                currentFrame = endFrame;
            }
        }
    }
}
