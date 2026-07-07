using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;

namespace GaussianSplatting.Runtime
{
    /// <summary>
    /// Manages cutout volumes for a GaussianSplatRenderer. Auto-collects
    /// child GaussianCutout components each frame. Attach to the same
    /// GameObject as the Renderer.
    /// </summary>
    [RequireComponent(typeof(GaussianSplatRenderer))]
    [ExecuteInEditMode]
    public class GaussianCutoutManager : MonoBehaviour
    {
        private GaussianSplatRenderer _renderer;
        private readonly List<GaussianCutout> _cutouts = new();
        private GraphicsBuffer _gpuCutoutsBuffer;

        public int CutoutCount => _cutouts.Count;
        public IReadOnlyList<GaussianCutout> Cutouts => _cutouts;

        private void OnEnable()
        {
            _renderer = GetComponent<GaussianSplatRenderer>();
        }

        private void OnDisable()
        {
            if (_renderer != null)
                _renderer.ClearCutoutData();
            DisposeBuffer();
        }

        private void CollectCutouts()
        {
            _cutouts.Clear();
            GetComponentsInChildren(_cutouts);
        }

        private void LateUpdate()
        {
            if (_renderer == null || !_renderer.HasValidAsset || !_renderer.HasValidRenderSetup)
            {
                if (_renderer != null)
                    _renderer.ClearCutoutData();
                return;
            }

            CollectCutouts();

            int count = _cutouts.Count;
            if (count == 0)
            {
                _renderer.ClearCutoutData();
                return;
            }

            EnsureBuffer(count);
            UploadData(count);
            _renderer.SetCutoutData(count, _gpuCutoutsBuffer);
        }

        private void EnsureBuffer(int count)
        {
            if (_gpuCutoutsBuffer != null && _gpuCutoutsBuffer.count >= count)
                return;

            _gpuCutoutsBuffer?.Dispose();
            _gpuCutoutsBuffer = new GraphicsBuffer(
                GraphicsBuffer.Target.Structured,
                count,
                UnsafeUtility.SizeOf<GaussianCutout.ShaderData>())
            {
                name = "GaussianCutouts"
            };
        }

        private void UploadData(int count)
        {
            var data = new NativeArray<GaussianCutout.ShaderData>(count, Allocator.Temp);
            var matrix = _renderer.transform.localToWorldMatrix;
            for (int i = 0; i < count; i++)
            {
                data[i] = GaussianCutout.GetShaderData(_cutouts[i], matrix);
            }
            _gpuCutoutsBuffer.SetData(data, 0, 0, count);
            data.Dispose();
        }

        private void DisposeBuffer()
        {
            _gpuCutoutsBuffer?.Dispose();
            _gpuCutoutsBuffer = null;
        }
    }
}
