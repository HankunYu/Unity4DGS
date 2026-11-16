using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // The buffers and passes of a hashed point grid (GaussianPointHash.hlsl)
    // in a compute shader that includes it: built from the shader's points
    // each frame, then read by the shader's own search kernels.
    internal sealed class GaussianPointHash
    {
        // Must match HASH_SCAN_GROUP_SIZE in GaussianPointHash.hlsl
        private const int ScanGroupSize = 1024;
        private const int GroupSize = 256;

        private static readonly int PropCellCount = Shader.PropertyToID("_HashCellCount");
        private static readonly int PropCellSize = Shader.PropertyToID("_HashCellSize");
        private static readonly int PropCells = Shader.PropertyToID("_HashCells");
        private static readonly int PropCellsRead = Shader.PropertyToID("_HashCellsRead");
        private static readonly int PropCellStarts = Shader.PropertyToID("_HashCellStarts");
        private static readonly int PropCellStartsRead = Shader.PropertyToID("_HashCellStartsRead");
        private static readonly int PropPointCells = Shader.PropertyToID("_HashPointCells");
        private static readonly int PropPointCellsRead = Shader.PropertyToID("_HashPointCellsRead");
        private static readonly int PropSorted = Shader.PropertyToID("_HashSorted");
        private static readonly int PropSortedRead = Shader.PropertyToID("_HashSortedRead");

        private readonly ComputeShader _shader;
        private readonly int _kernelClear;
        private readonly int _kernelCount;
        private readonly int _kernelScan;
        private readonly int _kernelScatter;

        private GraphicsBuffer _cells;
        private GraphicsBuffer _cellStarts;
        private GraphicsBuffer _pointCells;
        private GraphicsBuffer _sorted;

        public GaussianPointHash(ComputeShader shader)
        {
            _shader = shader;
            _kernelClear = shader.FindKernel("CSHashClear");
            _kernelCount = shader.FindKernel("CSHashCount");
            _kernelScan = shader.FindKernel("CSHashScan");
            _kernelScatter = shader.FindKernel("CSHashScatter");
        }

        public int PointCount => _pointCells != null ? _pointCells.count : 0;

        // Sized for this many points: about two cells per point keeps the
        // hash collisions rare
        public void Ensure(int pointCount)
        {
            pointCount = Mathf.Max(1, pointCount);
            if (_pointCells != null && _pointCells.count == pointCount)
                return;
            Dispose();
            int cellCount = Mathf.Max(ScanGroupSize, Mathf.NextPowerOfTwo(pointCount * 2));
            _cells = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellCount, 4) { name = "GaussianHashCells" };
            _cellStarts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, cellCount, 4) { name = "GaussianHashCellStarts" };
            _pointCells = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pointCount, 8) { name = "GaussianHashPointCells" };
            _sorted = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pointCount, 4) { name = "GaussianHashSorted" };
        }

        // Lists the points in cells cellSize wide. bindPoints binds what the
        // shader's HashPointCount, HashPointActive and HashPointPos read, for
        // the kernel it is given.
        public void Build(CommandBuffer cmb, float cellSize, Action<CommandBuffer, int> bindPoints)
        {
            int cellCount = _cells.count;
            int pointGroups = (_pointCells.count + GroupSize - 1) / GroupSize;
            cmb.SetComputeIntParam(_shader, PropCellCount, cellCount);
            cmb.SetComputeFloatParam(_shader, PropCellSize, cellSize);

            cmb.SetComputeBufferParam(_shader, _kernelClear, PropCells, _cells);
            cmb.DispatchCompute(_shader, _kernelClear, (cellCount + GroupSize - 1) / GroupSize, 1, 1);

            bindPoints(cmb, _kernelCount);
            cmb.SetComputeBufferParam(_shader, _kernelCount, PropCells, _cells);
            cmb.SetComputeBufferParam(_shader, _kernelCount, PropPointCells, _pointCells);
            cmb.DispatchCompute(_shader, _kernelCount, pointGroups, 1, 1);

            cmb.SetComputeBufferParam(_shader, _kernelScan, PropCellsRead, _cells);
            cmb.SetComputeBufferParam(_shader, _kernelScan, PropCellStarts, _cellStarts);
            cmb.DispatchCompute(_shader, _kernelScan, 1, 1, 1);

            bindPoints(cmb, _kernelScatter);
            cmb.SetComputeBufferParam(_shader, _kernelScatter, PropPointCellsRead, _pointCells);
            cmb.SetComputeBufferParam(_shader, _kernelScatter, PropCellStartsRead, _cellStarts);
            cmb.SetComputeBufferParam(_shader, _kernelScatter, PropSorted, _sorted);
            cmb.DispatchCompute(_shader, _kernelScatter, pointGroups, 1, 1);
        }

        // The finished grid, for a search kernel
        public void SetOnCS(CommandBuffer cmb, int kernelIndex)
        {
            cmb.SetComputeBufferParam(_shader, kernelIndex, PropCellsRead, _cells);
            cmb.SetComputeBufferParam(_shader, kernelIndex, PropCellStartsRead, _cellStarts);
            cmb.SetComputeBufferParam(_shader, kernelIndex, PropSortedRead, _sorted);
        }

        public void Dispose()
        {
            _cells?.Dispose();
            _cells = null;
            _cellStarts?.Dispose();
            _cellStarts = null;
            _pointCells?.Dispose();
            _pointCells = null;
            _sorted?.Dispose();
            _sorted = null;
        }
    }
}
