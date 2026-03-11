using System;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    internal class GaussianSplatEditManager : IDisposable
    {
        private readonly GaussianSplatRenderer _renderer;

        // GPU buffers owned by this manager
        private GraphicsBuffer _gpuEditCountsBounds;
        private GraphicsBuffer _gpuEditSelected;
        private GraphicsBuffer _gpuEditDeleted;
        private GraphicsBuffer _gpuEditSelectedMouseDown;
        private GraphicsBuffer _gpuEditPosMouseDown;
        private GraphicsBuffer _gpuEditOtherMouseDown;

        // Public state
        public bool Modified { get; internal set; }
        public uint SelectedSplats { get; private set; }
        public uint DeletedSplats { get; private set; }
        public uint CutSplats { get; private set; }
        public Bounds SelectedBounds { get; private set; }
        public GraphicsBuffer GpuEditDeleted => _gpuEditDeleted;
        internal GraphicsBuffer GpuEditSelected => _gpuEditSelected;
        public GaussianSplatEditManager(GaussianSplatRenderer renderer)
        {
            _renderer = renderer;
        }

        public void Dispose()
        {
            DisposeBuffer(ref _gpuEditCountsBounds);
            DisposeBuffer(ref _gpuEditSelected);
            DisposeBuffer(ref _gpuEditDeleted);
            DisposeBuffer(ref _gpuEditSelectedMouseDown);
            DisposeBuffer(ref _gpuEditPosMouseDown);
            DisposeBuffer(ref _gpuEditOtherMouseDown);

            SelectedSplats = 0;
            DeletedSplats = 0;
            CutSplats = 0;
            Modified = false;
            SelectedBounds = default;
        }

        private static void DisposeBuffer(ref GraphicsBuffer buf)
        {
            buf?.Dispose();
            buf = null;
        }

        private void ClearGraphicsBuffer(GraphicsBuffer buf)
        {
            if (!_renderer.TryFindSupportedKernel(GaussianSplatRenderer.KernelIndices.ClearBuffer, out int kernelIndex))
                return;
            _renderer.csSplatUtilities.SetBuffer(kernelIndex, GaussianSplatRenderer.Props.DstBuffer, buf);
            _renderer.csSplatUtilities.SetInt(GaussianSplatRenderer.Props.BufferSize, buf.count);
            _renderer.csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            _renderer.csSplatUtilities.Dispatch(kernelIndex, (int)((buf.count + gsX - 1) / gsX), 1, 1);
        }

        private void UnionGraphicsBuffers(GraphicsBuffer dst, GraphicsBuffer src)
        {
            if (!_renderer.TryFindSupportedKernel(GaussianSplatRenderer.KernelIndices.OrBuffers, out int kernelIndex))
                return;
            _renderer.csSplatUtilities.SetBuffer(kernelIndex, GaussianSplatRenderer.Props.SrcBuffer, src);
            _renderer.csSplatUtilities.SetBuffer(kernelIndex, GaussianSplatRenderer.Props.DstBuffer, dst);
            _renderer.csSplatUtilities.SetInt(GaussianSplatRenderer.Props.BufferSize, dst.count);
            _renderer.csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            _renderer.csSplatUtilities.Dispatch(kernelIndex, (int)((dst.count + gsX - 1) / gsX), 1, 1);
        }

        private static float SortableUintToFloat(uint v)
        {
            uint mask = ((v >> 31) - 1) | 0x80000000u;
            return math.asfloat(v ^ mask);
        }

        private bool DispatchUtilsAndExecute(CommandBuffer cmb, GaussianSplatRenderer.KernelIndices kernel, int count)
        {
            if (count <= 0)
            {
                Graphics.ExecuteCommandBuffer(cmb);
                return true;
            }
            if (!_renderer.TryFindSupportedKernel(kernel, out int kernelIndex))
                return false;
            _renderer.csSplatUtilities.GetKernelThreadGroupSizes(kernelIndex, out uint gsX, out _, out _);
            cmb.DispatchCompute(_renderer.csSplatUtilities, kernelIndex, (int)((count + gsX - 1) / gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);
            return true;
        }

        public void UpdateEditCountsAndBounds()
        {
            if (_gpuEditSelected == null)
            {
                SelectedSplats = 0;
                DeletedSplats = 0;
                CutSplats = 0;
                Modified = false;
                SelectedBounds = default;
                return;
            }

            if (!_renderer.TryFindSupportedKernel(GaussianSplatRenderer.KernelIndices.InitEditData, out int initKernel))
                return;
            _renderer.csSplatUtilities.SetBuffer(initKernel, GaussianSplatRenderer.Props.DstBuffer, _gpuEditCountsBounds);
            _renderer.csSplatUtilities.Dispatch(initKernel, 1, 1, 1);

            using CommandBuffer cmb = new CommandBuffer();
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.UpdateEditData, out int updateKernel))
                return;
            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, updateKernel, GaussianSplatRenderer.Props.DstBuffer, _gpuEditCountsBounds);
            cmb.SetComputeIntParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.BufferSize, _gpuEditSelected.count);
            _renderer.csSplatUtilities.GetKernelThreadGroupSizes(updateKernel, out uint gsX, out _, out _);
            cmb.DispatchCompute(_renderer.csSplatUtilities, updateKernel, (int)((_gpuEditSelected.count + gsX - 1) / gsX), 1, 1);
            Graphics.ExecuteCommandBuffer(cmb);

            uint[] res = new uint[_gpuEditCountsBounds.count];
            _gpuEditCountsBounds.GetData(res);
            SelectedSplats = res[0];
            DeletedSplats = res[1];
            CutSplats = res[2];
            Vector3 min = new Vector3(SortableUintToFloat(res[3]), SortableUintToFloat(res[4]), SortableUintToFloat(res[5]));
            Vector3 max = new Vector3(SortableUintToFloat(res[6]), SortableUintToFloat(res[7]), SortableUintToFloat(res[8]));
            Bounds bounds = default;
            bounds.SetMinMax(min, max);
            if (bounds.extents.sqrMagnitude < 0.01)
                bounds.extents = new Vector3(0.1f, 0.1f, 0.1f);
            SelectedBounds = bounds;
        }

        private bool EnsureEditingBuffers()
        {
            if (!_renderer.HasValidAsset || !_renderer.HasValidRenderSetup)
                return false;

            if (_gpuEditSelected == null)
            {
                var target = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource |
                             GraphicsBuffer.Target.CopyDestination;
                var size = (_renderer.SplatCount + 31) / 32;
                _gpuEditSelected = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatSelected" };
                _gpuEditSelectedMouseDown = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatSelectedInit" };
                _gpuEditDeleted = new GraphicsBuffer(target, size, 4) { name = "GaussianSplatDeleted" };
                _gpuEditCountsBounds = new GraphicsBuffer(target, 3 + 6, 4) { name = "GaussianSplatEditData" }; // selected count, deleted bound, cut count, float3 min, float3 max
                ClearGraphicsBuffer(_gpuEditSelected);
                ClearGraphicsBuffer(_gpuEditSelectedMouseDown);
                ClearGraphicsBuffer(_gpuEditDeleted);
            }
            return _gpuEditSelected != null;
        }

        public void StoreSelectionMouseDown()
        {
            if (!EnsureEditingBuffers()) return;
            Graphics.CopyBuffer(_gpuEditSelected, _gpuEditSelectedMouseDown);
        }

        public void StorePosMouseDown()
        {
            var posData = _renderer.GpuPosData;
            if (_gpuEditPosMouseDown == null)
            {
                _gpuEditPosMouseDown = new GraphicsBuffer(posData.target | GraphicsBuffer.Target.CopyDestination, posData.count, posData.stride) { name = "GaussianSplatEditPosMouseDown" };
            }
            Graphics.CopyBuffer(posData, _gpuEditPosMouseDown);
        }

        public void StoreOtherMouseDown()
        {
            var otherData = _renderer.GpuOtherData;
            if (_gpuEditOtherMouseDown == null)
            {
                _gpuEditOtherMouseDown = new GraphicsBuffer(otherData.target | GraphicsBuffer.Target.CopyDestination, otherData.count, otherData.stride) { name = "GaussianSplatEditOtherMouseDown" };
            }
            Graphics.CopyBuffer(otherData, _gpuEditOtherMouseDown);
        }

        public void UpdateSelection(Vector2 rectMin, Vector2 rectMax, Camera cam, bool subtract)
        {
            if (!EnsureEditingBuffers()) return;

            Graphics.CopyBuffer(_gpuEditSelectedMouseDown, _gpuEditSelected);

            var tr = _renderer.transform;
            Matrix4x4 matView = cam.worldToCameraMatrix;
            Matrix4x4 matO2W = tr.localToWorldMatrix;
            Matrix4x4 matW2O = tr.worldToLocalMatrix;
            int screenW = cam.pixelWidth, screenH = cam.pixelHeight;
            Vector4 screenPar = new Vector4(screenW, screenH, 0, 0);
            Vector4 camPos = cam.transform.position;

            using var cmb = new CommandBuffer { name = "SplatSelectionUpdate" };
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.SelectionUpdate, out _))
                return;

            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.MatrixMV, matView * matO2W);
            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.MatrixObjectToWorld, matO2W);
            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.MatrixWorldToObject, matW2O);

            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.VecScreenParams, screenPar);
            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.VecWorldSpaceCameraPos, camPos);

            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.SelectionRect, new Vector4(rectMin.x, rectMax.y, rectMax.x, rectMin.y));
            cmb.SetComputeIntParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.SelectionMode, subtract ? 0 : 1);

            if (!DispatchUtilsAndExecute(cmb, GaussianSplatRenderer.KernelIndices.SelectionUpdate, _renderer.SplatCount))
                return;
            UpdateEditCountsAndBounds();
        }

        public void TranslateSelection(Vector3 localSpacePosDelta)
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatTranslateSelection" };
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.TranslateSelection, out _))
                return;

            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.SelectionDelta, localSpacePosDelta);

            if (!DispatchUtilsAndExecute(cmb, GaussianSplatRenderer.KernelIndices.TranslateSelection, _renderer.SplatCount))
                return;
            UpdateEditCountsAndBounds();
            Modified = true;
        }

        public void RotateSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Quaternion rotation)
        {
            if (!EnsureEditingBuffers()) return;
            if (_gpuEditPosMouseDown == null || _gpuEditOtherMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatRotateSelection" };
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.RotateSelection, out int kernelIndex))
                return;

            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, GaussianSplatRenderer.Props.SplatPosMouseDown, _gpuEditPosMouseDown);
            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, GaussianSplatRenderer.Props.SplatOtherMouseDown, _gpuEditOtherMouseDown);
            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.SelectionDeltaRot, new Vector4(rotation.x, rotation.y, rotation.z, rotation.w));

            if (!DispatchUtilsAndExecute(cmb, GaussianSplatRenderer.KernelIndices.RotateSelection, _renderer.SplatCount))
                return;
            UpdateEditCountsAndBounds();
            Modified = true;
        }

        public void ScaleSelection(Vector3 localSpaceCenter, Matrix4x4 localToWorld, Matrix4x4 worldToLocal, Vector3 scale)
        {
            if (!EnsureEditingBuffers()) return;
            if (_gpuEditPosMouseDown == null) return; // should have captured initial state

            using var cmb = new CommandBuffer { name = "SplatScaleSelection" };
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.ScaleSelection, out int kernelIndex))
                return;

            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, GaussianSplatRenderer.Props.SplatPosMouseDown, _gpuEditPosMouseDown);
            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.SelectionCenter, localSpaceCenter);
            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.MatrixObjectToWorld, localToWorld);
            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.MatrixWorldToObject, worldToLocal);
            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.SelectionDelta, scale);

            if (!DispatchUtilsAndExecute(cmb, GaussianSplatRenderer.KernelIndices.ScaleSelection, _renderer.SplatCount))
                return;
            UpdateEditCountsAndBounds();
            Modified = true;
        }

        public void DeleteSelected()
        {
            if (!EnsureEditingBuffers()) return;
            UnionGraphicsBuffers(_gpuEditDeleted, _gpuEditSelected);
            DeselectAll();
            UpdateEditCountsAndBounds();
            if (DeletedSplats != 0)
                Modified = true;
        }

        public void SelectAll()
        {
            if (!EnsureEditingBuffers()) return;
            using var cmb = new CommandBuffer { name = "SplatSelectAll" };
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.SelectAll, out int kernelIndex))
                return;
            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, GaussianSplatRenderer.Props.DstBuffer, _gpuEditSelected);
            cmb.SetComputeIntParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.BufferSize, _gpuEditSelected.count);
            if (!DispatchUtilsAndExecute(cmb, GaussianSplatRenderer.KernelIndices.SelectAll, _gpuEditSelected.count))
                return;
            UpdateEditCountsAndBounds();
        }

        public void DeselectAll()
        {
            if (!EnsureEditingBuffers()) return;
            ClearGraphicsBuffer(_gpuEditSelected);
            UpdateEditCountsAndBounds();
        }

        public void InvertSelection()
        {
            if (!EnsureEditingBuffers()) return;

            using var cmb = new CommandBuffer { name = "SplatInvertSelection" };
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.InvertSelection, out int kernelIndex))
                return;
            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, GaussianSplatRenderer.Props.DstBuffer, _gpuEditSelected);
            cmb.SetComputeIntParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.BufferSize, _gpuEditSelected.count);
            if (!DispatchUtilsAndExecute(cmb, GaussianSplatRenderer.KernelIndices.InvertSelection, _gpuEditSelected.count))
                return;
            UpdateEditCountsAndBounds();
        }

        public bool ExportData(GraphicsBuffer dstData, bool bakeTransform)
        {
            if (!EnsureEditingBuffers()) return false;

            int flags = 0;
            var tr = _renderer.transform;
            Quaternion bakeRot = tr.localRotation;
            Vector3 bakeScale = tr.localScale;

            if (bakeTransform)
                flags = 1;

            using var cmb = new CommandBuffer { name = "SplatExportData" };
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.ExportData, out int kernelIndex))
                return false;
            cmb.SetComputeIntParam(_renderer.csSplatUtilities, "_ExportTransformFlags", flags);
            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, "_ExportTransformRotation", new Vector4(bakeRot.x, bakeRot.y, bakeRot.z, bakeRot.w));
            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, "_ExportTransformScale", bakeScale);
            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, GaussianSplatRenderer.Props.MatrixObjectToWorld, tr.localToWorldMatrix);
            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, "_ExportBuffer", dstData);

            if (!DispatchUtilsAndExecute(cmb, GaussianSplatRenderer.KernelIndices.ExportData, _renderer.SplatCount))
                return false;
            return true;
        }

        public void SetSplatCount(int newSplatCount)
        {
            if (newSplatCount <= 0 || newSplatCount > GaussianSplatAsset.MaxSplats)
            {
                Debug.LogError($"Invalid new splat count: {newSplatCount}");
                return;
            }
            if (_renderer.asset.chunkData != null)
            {
                Debug.LogError("Only splats with VeryHigh quality can be resized");
                return;
            }
            if (newSplatCount == _renderer.splatCount)
                return;

            int posStride = (int)(_renderer.asset.posData.dataSize / _renderer.asset.splatCount);
            int otherStride = (int)(_renderer.asset.otherData.dataSize / _renderer.asset.splatCount);
            int shStride = (int)(_renderer.asset.shData.dataSize / _renderer.asset.splatCount);

            // create new GPU buffers
            var newPosData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * posStride / 4, 4) { name = "GaussianPosData" };
            var newOtherData = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource, newSplatCount * otherStride / 4, 4) { name = "GaussianOtherData" };
            var newSHData = new GraphicsBuffer(GraphicsBuffer.Target.Raw, newSplatCount * shStride / 4, 4) { name = "GaussianSHData" };

            // new texture is a RenderTexture so we can write to it from a compute shader
            var (texWidth, texHeight) = GaussianSplatAsset.CalcTextureSize(newSplatCount);
            var texFormat = GaussianSplatAsset.ColorFormatToGraphics(_renderer.asset.colorFormat);
            var newColorData = new RenderTexture(texWidth, texHeight, texFormat, UnityEngine.Experimental.Rendering.GraphicsFormat.None) { name = "GaussianColorData", enableRandomWrite = true };
            newColorData.Create();

            // selected/deleted buffers
            var selTarget = GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.CopySource | GraphicsBuffer.Target.CopyDestination;
            var selSize = (newSplatCount + 31) / 32;
            var newEditSelected = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatSelected" };
            var newEditSelectedMouseDown = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatSelectedInit" };
            var newEditDeleted = new GraphicsBuffer(selTarget, selSize, 4) { name = "GaussianSplatDeleted" };
            ClearGraphicsBuffer(newEditSelected);
            ClearGraphicsBuffer(newEditSelectedMouseDown);
            ClearGraphicsBuffer(newEditDeleted);

            var newGpuView = new GraphicsBuffer(GraphicsBuffer.Target.Structured, newSplatCount * 2, GaussianSplatRenderer.GpuViewDataSize);
            _renderer.InitSortBuffers(newSplatCount);

            // copy existing data over into new buffers
            CopySplats(_renderer.transform, newPosData, newOtherData, newSHData, newColorData, newEditDeleted, newSplatCount, 0, 0, _renderer.SplatCount);

            // use the new buffers and the new splat count
            _renderer._gpuPosData.Dispose();
            _renderer._gpuOtherData.Dispose();
            _renderer._gpuSHData.Dispose();
            UnityEngine.Object.DestroyImmediate(_renderer._gpuColorData);
            _renderer._gpuView.Dispose();

            _gpuEditSelected?.Dispose();
            _gpuEditSelectedMouseDown?.Dispose();
            _gpuEditDeleted?.Dispose();

            _renderer._gpuPosData = newPosData;
            _renderer._gpuOtherData = newOtherData;
            _renderer._gpuSHData = newSHData;
            _renderer._gpuColorData = newColorData;
            _renderer._gpuView = newGpuView;
            _gpuEditSelected = newEditSelected;
            _gpuEditSelectedMouseDown = newEditSelectedMouseDown;
            _gpuEditDeleted = newEditDeleted;

            DisposeBuffer(ref _gpuEditPosMouseDown);
            DisposeBuffer(ref _gpuEditOtherMouseDown);

            _renderer.SplatCount = newSplatCount;
            Modified = true;
        }

        public void CopySplatsInto(GaussianSplatRenderer dst, int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            CopySplats(
                dst.transform,
                dst._gpuPosData, dst._gpuOtherData, dst._gpuSHData, dst._gpuColorData, dst.EditManager.GpuEditDeleted,
                dst.splatCount,
                copySrcStartIndex, copyDstStartIndex, copyCount);
            dst.EditManager.Modified = true;
        }

        public void CopySplats(
            Transform dstTransform,
            GraphicsBuffer dstPos, GraphicsBuffer dstOther, GraphicsBuffer dstSH, Texture dstColor,
            GraphicsBuffer dstEditDeleted,
            int dstSize,
            int copySrcStartIndex, int copyDstStartIndex, int copyCount)
        {
            if (!EnsureEditingBuffers()) return;

            Matrix4x4 copyMatrix = dstTransform.worldToLocalMatrix * _renderer.transform.localToWorldMatrix;
            Quaternion copyRot = copyMatrix.rotation;
            Vector3 copyScale = copyMatrix.lossyScale;

            using var cmb = new CommandBuffer { name = "SplatCopy" };
            if (!_renderer.SetAssetDataOnCS(cmb, GaussianSplatRenderer.KernelIndices.CopySplats, out int kernelIndex))
                return;

            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, "_CopyDstPos", dstPos);
            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, "_CopyDstOther", dstOther);
            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, "_CopyDstSH", dstSH);
            cmb.SetComputeTextureParam(_renderer.csSplatUtilities, kernelIndex, "_CopyDstColor", dstColor);
            cmb.SetComputeBufferParam(_renderer.csSplatUtilities, kernelIndex, "_CopyDstEditDeleted", dstEditDeleted);

            cmb.SetComputeIntParam(_renderer.csSplatUtilities, "_CopyDstSize", dstSize);
            cmb.SetComputeIntParam(_renderer.csSplatUtilities, "_CopySrcStartIndex", copySrcStartIndex);
            cmb.SetComputeIntParam(_renderer.csSplatUtilities, "_CopyDstStartIndex", copyDstStartIndex);
            cmb.SetComputeIntParam(_renderer.csSplatUtilities, "_CopyCount", copyCount);

            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, "_CopyTransformRotation", new Vector4(copyRot.x, copyRot.y, copyRot.z, copyRot.w));
            cmb.SetComputeVectorParam(_renderer.csSplatUtilities, "_CopyTransformScale", copyScale);
            cmb.SetComputeMatrixParam(_renderer.csSplatUtilities, "_CopyTransformMatrix", copyMatrix);

            DispatchUtilsAndExecute(cmb, GaussianSplatRenderer.KernelIndices.CopySplats, copyCount);
        }
    }
}
