using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Main controller for procedural splat animation. Attach to the same
    /// GameObject as GaussianSplatRenderer. Auto-collects child
    /// GaussianAnimVolume objects, packs their data to GPU, and dispatches
    /// the animation compute shader each frame.
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [ExecuteInEditMode]
    public class GaussianAnimator : MonoBehaviour
    {
        private ComputeShader _animateShader;
        private GaussianSplatRenderer _renderer;
        private GraphicsBuffer _animOutputBuffer;
        private GraphicsBuffer _activationBuffer;

        // Auto-collected from children each frame
        private readonly List<GaussianAnimVolume> _volumes = new();
        public IReadOnlyList<GaussianAnimVolume> Volumes => _volumes;

        // Structured buffers for GPU upload
        private GraphicsBuffer _volumeBuffer;
        private GraphicsBuffer _modifierBuffer;

        private int _kernelAnimate = -1;
        private int _lastSplatCount;
        private int _activeSplatCount;
        private float[] _zeroActivation;

        // Cached shader property IDs
        private static readonly int PropAnimVolumes = Shader.PropertyToID("_AnimVolumes");
        private static readonly int PropAnimModifiers = Shader.PropertyToID("_AnimModifiers");
        private static readonly int PropAnimVolumeCount = Shader.PropertyToID("_AnimVolumeCount");
        private static readonly int PropAnimModifierCount = Shader.PropertyToID("_AnimModifierCount");
        private static readonly int PropAnimSplatCount = Shader.PropertyToID("_AnimSplatCount");
        private static readonly int PropAnimSplatFormat = Shader.PropertyToID("_AnimSplatFormat");
        private static readonly int PropAnimChunkCount = Shader.PropertyToID("_AnimChunkCount");
        private static readonly int PropAnimSplatPos = Shader.PropertyToID("_AnimSplatPos");
        private static readonly int PropAnimSplatChunks = Shader.PropertyToID("_AnimSplatChunks");
        private static readonly int PropAnimOutput = Shader.PropertyToID("_AnimOutput");
        private static readonly int PropAnimMatrixO2W = Shader.PropertyToID("_AnimMatrixObjectToWorld");
        private static readonly int PropAnimActivation = Shader.PropertyToID("_AnimActivation");
        private static readonly int PropAnimDeltaTime = Shader.PropertyToID("_AnimDeltaTime");

        // Must match struct sizes in compute shader
        const int VolumeSizeBytes = 64 + 64 + 16 + 16; // 2x float4x4 + float4 + float4 = 160
        const int ModifierSizeBytes = 16 + 16 + 16 + 16 + 16; // 2 ints + 2 floats + 4 float4s = 80

        const int MaxVolumes = 16;
        const int MaxModifiers = 64;

        private void OnEnable()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
            if (_animateShader == null)
                _animateShader = Resources.Load<ComputeShader>("GaussianAnimate");
        }

        private void OnDisable()
        {
            ReleaseBuffers();
            if (_renderer != null)
                _renderer.ClearAnimationOutput();
        }

        private void ReleaseBuffers()
        {
            _animOutputBuffer?.Dispose();
            _animOutputBuffer = null;
            _activationBuffer?.Dispose();
            _activationBuffer = null;
            _volumeBuffer?.Dispose();
            _volumeBuffer = null;
            _modifierBuffer?.Dispose();
            _modifierBuffer = null;
            _kernelAnimate = -1;
        }

        private void CollectVolumes()
        {
            _volumes.Clear();
            GetComponentsInChildren(_volumes);
        }

        private void LateUpdate()
        {
            if (_renderer == null || !_renderer.HasValidAsset || !_renderer.HasValidRenderSetup)
            {
                if (_renderer != null)
                    _renderer.ClearAnimationOutput();
                return;
            }

            if (_animateShader == null)
            {
                _renderer.ClearAnimationOutput();
                return;
            }

            CollectVolumes();

            if (_volumes.Count == 0)
            {
                _renderer.ClearAnimationOutput();
                return;
            }

            // Count active volumes and modifiers
            int volumeCount = 0;
            int modifierCount = 0;
            for (int i = 0; i < _volumes.Count && volumeCount < MaxVolumes; i++)
            {
                if (!_volumes[i].isActiveAndEnabled) continue;
                if (_volumes[i].transform.lossyScale.sqrMagnitude < 1e-6f) continue;
                volumeCount++;
                var mods = _volumes[i].GetModifiers();
                for (int j = 0; j < mods.Length && modifierCount < MaxModifiers; j++)
                {
                    if (mods[j] != null && mods[j].isActiveAndEnabled)
                        modifierCount++;
                }
            }

            if (volumeCount == 0 || modifierCount == 0)
            {
                _renderer.ClearAnimationOutput();
                return;
            }

            int splatCount = _renderer.splatCount;
            EnsureBuffers(splatCount, volumeCount, modifierCount);

            if (_kernelAnimate < 0)
                return;

            // Upload volume data
            UploadVolumeAndModifierData(volumeCount, modifierCount);

            // Dispatch compute
            DispatchAnimation(splatCount, volumeCount, modifierCount);

            // Set the output buffer on the renderer so CalcViewData can read it
            _renderer.SetAnimationOutput(_animOutputBuffer);
        }

        private void EnsureBuffers(int splatCount, int volumeCount, int modifierCount)
        {
            // Find kernel
            if (_kernelAnimate < 0)
            {
                if (!_animateShader.HasKernel("CSAnimateSplats"))
                    return;
                _kernelAnimate = _animateShader.FindKernel("CSAnimateSplats");
            }

            // Animation output: 3 float4s per splat
            if (_animOutputBuffer == null || _lastSplatCount < splatCount)
            {
                _animOutputBuffer?.Dispose();
                _animOutputBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount * 3, 16)
                {
                    name = "GaussianAnimOutput"
                };

                // Per-splat activation for trail persistence (1 float per splat)
                _activationBuffer?.Dispose();
                _activationBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, splatCount, 4)
                {
                    name = "GaussianAnimActivation"
                };
                // Zero-initialize so no residual trail on first frame
                _zeroActivation = new float[splatCount];
                _activationBuffer.SetData(_zeroActivation);

                _lastSplatCount = splatCount;
            }
            else if (splatCount < _activeSplatCount)
            {
                // Swapped to a smaller asset within existing capacity: clear the
                // reused range so the new asset does not inherit the previous
                // asset's per-index activation (phantom trails).
                _activationBuffer.SetData(_zeroActivation, 0, 0, splatCount);
            }
            _activeSplatCount = splatCount;

            // Volume buffer
            if (_volumeBuffer == null || _volumeBuffer.count < volumeCount)
            {
                _volumeBuffer?.Dispose();
                _volumeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(volumeCount, 1), VolumeSizeBytes)
                {
                    name = "GaussianAnimVolumes"
                };
            }

            // Modifier buffer
            if (_modifierBuffer == null || _modifierBuffer.count < modifierCount)
            {
                _modifierBuffer?.Dispose();
                _modifierBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(modifierCount, 1), ModifierSizeBytes)
                {
                    name = "GaussianAnimModifiers"
                };
            }
        }

        private void UploadVolumeAndModifierData(int volumeCount, int modifierCount)
        {
            // Pack volume data
            var volData = new NativeArray<GaussianAnimVolume.ShaderData>(volumeCount, Allocator.Temp);
            // Pack modifier data
            var modData = new NativeArray<ModifierShaderData>(modifierCount, Allocator.Temp);

            float time = Time.time;
#if UNITY_EDITOR
            if (!Application.isPlaying)
                time = (float)UnityEditor.EditorApplication.timeSinceStartup;
#endif

            int vi = 0;
            int mi = 0;
            for (int i = 0; i < _volumes.Count && vi < MaxVolumes; i++)
            {
                if (!_volumes[i].isActiveAndEnabled) continue;
                if (_volumes[i].transform.lossyScale.sqrMagnitude < 1e-6f) continue;
                volData[vi] = _volumes[i].GetShaderData();

                var mods = _volumes[i].GetModifiers();
                for (int j = 0; j < mods.Length && mi < MaxModifiers; j++)
                {
                    if (mods[j] == null || !mods[j].isActiveAndEnabled) continue;
                    mods[j].FillParams(time, out Vector4 p0, out Vector4 p1, out Vector4 p2, out Vector4 p3);
                    modData[mi] = new ModifierShaderData
                    {
                        volumeIndex = vi,
                        modifierType = mods[j].ModifierType,
                        pad0 = 0,
                        pad1 = 0,
                        params0 = p0,
                        params1 = p1,
                        params2 = p2,
                        params3 = p3
                    };
                    mi++;
                }
                vi++;
            }

            _volumeBuffer.SetData(volData, 0, 0, volumeCount);
            _modifierBuffer.SetData(modData, 0, 0, modifierCount);

            volData.Dispose();
            modData.Dispose();
        }

        private void DispatchAnimation(int splatCount, int volumeCount, int modifierCount)
        {
            var cs = _animateShader;
            int kernel = _kernelAnimate;

            cs.SetBuffer(kernel, PropAnimVolumes, _volumeBuffer);
            cs.SetBuffer(kernel, PropAnimModifiers, _modifierBuffer);
            cs.SetBuffer(kernel, PropAnimOutput, _animOutputBuffer);
            cs.SetInt(PropAnimVolumeCount, volumeCount);
            cs.SetInt(PropAnimModifierCount, modifierCount);
            cs.SetInt(PropAnimSplatCount, splatCount);

            var asset = _renderer.asset;
            uint format = (uint)asset.posFormat | ((uint)asset.scaleFormat << 8) | ((uint)asset.shFormat << 16);
            cs.SetInt(PropAnimSplatFormat, (int)format);

            bool hasChunks = _renderer.GpuChunksValid;
            int chunkCount = hasChunks ? _renderer.GpuChunksBuffer.count : 0;
            cs.SetInt(PropAnimChunkCount, chunkCount);

            // Set original pos buffer and chunk data
            cs.SetBuffer(kernel, PropAnimSplatPos, _renderer.GpuPosData);
            cs.SetBuffer(kernel, PropAnimSplatChunks, _renderer.GpuChunksBuffer);

            cs.SetMatrix(PropAnimMatrixO2W, transform.localToWorldMatrix);

            cs.SetBuffer(kernel, PropAnimActivation, _activationBuffer);
            cs.SetFloat(PropAnimDeltaTime, Time.deltaTime);

            cs.GetKernelThreadGroupSizes(kernel, out uint gsX, out _, out _);
            cs.Dispatch(kernel, (splatCount + (int)gsX - 1) / (int)gsX, 1, 1);
        }

        // Must match AnimModifierData in compute shader
        struct ModifierShaderData
        {
            public int volumeIndex;
            public int modifierType;
            public float pad0;
            public float pad1;
            public Vector4 params0;
            public Vector4 params1;
            public Vector4 params2;
            public Vector4 params3;
        }
    }
}
