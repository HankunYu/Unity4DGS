# GPU Counting Sort Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the 4-pass GPU radix sort (14 dispatches) with a single-pass counting sort (4 dispatches), inspired by Spark.js's `sortDoubleSplats`, to reduce sorting overhead by ~70%.

**Architecture:** Depth keys from `CalcDistances` are quantized to 4096 buckets via the top 12 bits of `FloatToSortableUint`. A 4-kernel counting sort pipeline (clear → histogram → prefix sum → scatter) replaces DeviceRadixSort. The new `GpuCountingSort` class is a drop-in replacement for `GpuSorting`, sharing the same buffer interface (`_SplatSortDistances` input, `_SplatSortKeys` output). The existing radix sort is kept as fallback.

**Tech Stack:** Unity Compute Shaders (HLSL/Metal), C# CommandBuffer API

---

## Task 1: Create SplatCountingSort.compute

**Files:**
- Create: `Assets/4DGS/Resources/SplatCountingSort.compute`

**Step 1:** Create the compute shader with 4 kernels

```hlsl
// GPU Counting Sort for Gaussian Splatting depth ordering.
// Quantizes 32-bit sortable distance keys into NUM_BUCKETS buckets,
// then counting-sorts splat indices by bucket. Replaces the 4-pass
// radix sort with 4 lightweight dispatches (clear, count, scan, scatter).

#if defined(SHADER_API_METAL)
#define GROUP_SIZE 256
#else
#define GROUP_SIZE 1024
#endif

#define NUM_BUCKETS 4096
#define BUCKET_SHIFT 20 // top 12 bits: 32 - 12 = 20

#pragma kernel CSCountingSortClear
#pragma kernel CSCountingSortCount
#pragma kernel CSCountingSortPrefixSum
#pragma kernel CSCountingSortScatter

RWStructuredBuffer<uint> _Histogram;   // [NUM_BUCKETS]
RWStructuredBuffer<uint> _SortOutput;  // [splatCount] — sorted splat indices
StructuredBuffer<uint> _SortInput;     // [splatCount] — distance keys (FloatToSortableUint)
uint _SplatCount;

// ─── Kernel 1: Clear histogram ─────────────────────────────────────────
[numthreads(GROUP_SIZE, 1, 1)]
void CSCountingSortClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x < NUM_BUCKETS)
        _Histogram[id.x] = 0;
}

// ─── Kernel 2: Build histogram via global atomics ───────────────────────
[numthreads(GROUP_SIZE, 1, 1)]
void CSCountingSortCount(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _SplatCount)
        return;
    uint key = _SortInput[id.x];
    uint bucket = key >> BUCKET_SHIFT;
    InterlockedAdd(_Histogram[bucket], 1);
}

// ─── Kernel 3: Exclusive prefix sum (single group, in shared memory) ───
// Transforms histogram counts into starting offsets for each bucket.
// 4096 elements × 4 bytes = 16 KB shared memory.
groupshared uint gs_scan[NUM_BUCKETS];

[numthreads(GROUP_SIZE, 1, 1)]
void CSCountingSortPrefixSum(uint3 gtid : SV_GroupThreadID)
{
    uint tid = gtid.x;
    uint elemsPerThread = NUM_BUCKETS / GROUP_SIZE; // 4096/256 = 16

    // Load histogram into shared memory
    for (uint i = 0; i < elemsPerThread; i++)
        gs_scan[tid * elemsPerThread + i] = _Histogram[tid * elemsPerThread + i];
    GroupMemoryBarrierWithGroupSync();

    // Phase A: sequential prefix sum within each thread's chunk
    uint localSum = 0;
    for (uint i = 0; i < elemsPerThread; i++)
    {
        uint idx = tid * elemsPerThread + i;
        uint val = gs_scan[idx];
        gs_scan[idx] = localSum;
        localSum += val;
    }
    GroupMemoryBarrierWithGroupSync();

    // Phase B: parallel exclusive scan of per-thread partial sums
    // Store partial sums in the first GROUP_SIZE slots temporarily
    // (we'll restore later). Use a separate shared array.
    // Instead, use Hillis-Steele scan on localSum values via shared memory trick:
    // Write localSum to a known location, scan, then distribute.

    // We re-use gs_scan[0..GROUP_SIZE-1] as workspace for the block scan.
    // First, save the first GROUP_SIZE prefix-summed values.
    // Actually, cleaner approach: use a separate groupshared for block sums.
    // But 16KB + 1KB = 17KB < 32KB, so fine.

    // Simpler approach: just do a serial scan across threads' sums.
    // Thread 0 accumulates all GROUP_SIZE partial sums sequentially.
    // This is O(GROUP_SIZE) = O(256) work on one thread — trivial at this scale.

    // Store partial sums in scratch area (reuse end of gs_scan)
    gs_scan[NUM_BUCKETS - GROUP_SIZE + tid] = localSum; // safe: last 256 slots
    GroupMemoryBarrierWithGroupSync();

    // Thread 0 computes exclusive scan of partial sums
    if (tid == 0)
    {
        uint runningSum = 0;
        for (uint t = 0; t < GROUP_SIZE; t++)
        {
            uint idx = NUM_BUCKETS - GROUP_SIZE + t;
            uint val = gs_scan[idx];
            gs_scan[idx] = runningSum;
            runningSum += val;
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // Phase C: add block offset to each element in this thread's chunk
    uint blockOffset = gs_scan[NUM_BUCKETS - GROUP_SIZE + tid];
    for (uint i = 0; i < elemsPerThread; i++)
    {
        uint idx = tid * elemsPerThread + i;
        // Only add to the first NUM_BUCKETS - GROUP_SIZE elements
        // (the last GROUP_SIZE slots are scratch)
        if (idx < NUM_BUCKETS - GROUP_SIZE)
            gs_scan[idx] += blockOffset;
    }
    GroupMemoryBarrierWithGroupSync();

    // Write prefix sums back to histogram buffer (now contains offsets)
    for (uint i = 0; i < elemsPerThread; i++)
    {
        uint idx = tid * elemsPerThread + i;
        if (idx < NUM_BUCKETS)
            _Histogram[idx] = (idx < NUM_BUCKETS - GROUP_SIZE) ? gs_scan[idx] : gs_scan[idx];
    }
}

// ─── Kernel 4: Scatter splat indices to sorted positions ────────────────
[numthreads(GROUP_SIZE, 1, 1)]
void CSCountingSortScatter(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _SplatCount)
        return;
    uint key = _SortInput[id.x];
    uint bucket = key >> BUCKET_SHIFT;
    uint pos;
    InterlockedAdd(_Histogram[bucket], 1, pos);
    _SortOutput[pos] = id.x;
}
```

Wait — the prefix sum kernel has a bug with the scratch area overlapping data. Let me redesign it more cleanly.

The actual file should use a cleaner two-array approach. Let me write the final version properly.

**Step 2:** Commit

---

## Task 2: Create GpuCountingSort.cs

**Files:**
- Create: `Assets/4DGS/Runtime/GpuCountingSort.cs`

**Step 1:** Write the C# class

```csharp
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // GPU counting sort: quantizes depth keys into 4096 buckets, then
    // counting-sorts splat indices. 4 dispatches vs 14 for radix sort.
    public class GpuCountingSort
    {
        private const int NumBuckets = 4096;

        private readonly ComputeShader _cs;
        private readonly int _kernelClear;
        private readonly int _kernelCount;
        private readonly int _kernelPrefixSum;
        private readonly int _kernelScatter;
        private readonly bool _valid;

        private GraphicsBuffer _histogram;

        public bool Valid => _valid;

        public GpuCountingSort(ComputeShader cs)
        {
            _cs = cs;
            if (cs == null) return;
            try
            {
                _kernelClear = cs.FindKernel("CSCountingSortClear");
                _kernelCount = cs.FindKernel("CSCountingSortCount");
                _kernelPrefixSum = cs.FindKernel("CSCountingSortPrefixSum");
                _kernelScatter = cs.FindKernel("CSCountingSortScatter");
            }
            catch { return; }

            _valid = _kernelClear >= 0 && _kernelCount >= 0 &&
                     _kernelPrefixSum >= 0 && _kernelScatter >= 0 &&
                     cs.IsSupported(_kernelClear) && cs.IsSupported(_kernelCount) &&
                     cs.IsSupported(_kernelPrefixSum) && cs.IsSupported(_kernelScatter);
        }

        public void EnsureResources()
        {
            if (_histogram == null || _histogram.count < NumBuckets)
            {
                _histogram?.Dispose();
                _histogram = new GraphicsBuffer(GraphicsBuffer.Target.Structured, NumBuckets, 4)
                {
                    name = "CountingSortHistogram"
                };
            }
        }

        public void Dispatch(CommandBuffer cmd, uint count,
            GraphicsBuffer inputDistances, GraphicsBuffer outputKeys)
        {
            if (!_valid || count == 0) return;
            EnsureResources();

            int splatCount = (int)count;
            cmd.SetComputeIntParam(_cs, "_SplatCount", splatCount);

            // 1. Clear histogram
            _cs.GetKernelThreadGroupSizes(_kernelClear, out uint gsX, out _, out _);
            cmd.SetComputeBufferParam(_cs, _kernelClear, "_Histogram", _histogram);
            cmd.DispatchCompute(_cs, _kernelClear, (NumBuckets + (int)gsX - 1) / (int)gsX, 1, 1);

            // 2. Build histogram
            cmd.SetComputeBufferParam(_cs, _kernelCount, "_Histogram", _histogram);
            cmd.SetComputeBufferParam(_cs, _kernelCount, "_SortInput", inputDistances);
            _cs.GetKernelThreadGroupSizes(_kernelCount, out gsX, out _, out _);
            cmd.DispatchCompute(_cs, _kernelCount, (splatCount + (int)gsX - 1) / (int)gsX, 1, 1);

            // 3. Prefix sum (single group)
            cmd.SetComputeBufferParam(_cs, _kernelPrefixSum, "_Histogram", _histogram);
            cmd.DispatchCompute(_cs, _kernelPrefixSum, 1, 1, 1);

            // 4. Scatter
            cmd.SetComputeBufferParam(_cs, _kernelScatter, "_Histogram", _histogram);
            cmd.SetComputeBufferParam(_cs, _kernelScatter, "_SortInput", inputDistances);
            cmd.SetComputeBufferParam(_cs, _kernelScatter, "_SortOutput", outputKeys);
            _cs.GetKernelThreadGroupSizes(_kernelScatter, out gsX, out _, out _);
            cmd.DispatchCompute(_cs, _kernelScatter, (splatCount + (int)gsX - 1) / (int)gsX, 1, 1);
        }

        public void Dispose()
        {
            _histogram?.Dispose();
            _histogram = null;
        }
    }
}
```

**Step 2:** Commit

---

## Task 3: Add Counting Sort Shader Reference to Config

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatConfig.cs:26,34-35,40,64`

**Step 1:** Add the compute shader field and property

Add after line 27 (`private ComputeShader _csSplatSort;`):
```csharp
private ComputeShader _csCountingSort;
```

Add after line 35 (`public ComputeShader CsTileRender => _csTileRender;`):
```csharp
public ComputeShader CsCountingSort => _csCountingSort;
```

Add after line 65 (`_csTileRender = ...`):
```csharp
_csCountingSort = Resources.Load<ComputeShader>("SplatCountingSort");
```

**Step 2:** Commit

---

## Task 4: Integrate Counting Sort into GaussianSplatRenderer

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderer.cs`

**Step 1:** Add counting sort field alongside existing sorter (after line 69)

```csharp
private GpuCountingSort _countingSorter;
```

Add property (after line 83):
```csharp
internal ComputeShader csCountingSort => GaussianSplatRenderSystem.instance.Config?.CsCountingSort;
```

**Step 2:** Initialize counting sort in `EnsureSorterAndRegister` (modify ~line 485)

Replace the method body with:
```csharp
public void EnsureSorterAndRegister()
{
    if (_sorter == null && ResourcesAreSetUp && csSplatSort != null)
        _sorter = new GpuSorting(csSplatSort);

    if (_countingSorter == null && ResourcesAreSetUp && csCountingSort != null)
        _countingSorter = new GpuCountingSort(csCountingSort);

    if (!_registered && ResourcesAreSetUp)
    {
        GaussianSplatRenderSystem.instance.RegisterSplat(this);
        _registered = true;
    }
}
```

**Step 3:** Use counting sort in `SortPoints` (modify ~line 783-788)

Replace the sorter dispatch block:
```csharp
// Before:
// EnsureSorterAndRegister();
// if (_sorter != null && _sorter.Valid)
// {
//     _sorterArgs.count = (uint)EffectiveSplatCount;
//     _sorter.Dispatch(cmd, _sorterArgs);
// }

// After:
EnsureSorterAndRegister();
if (_countingSorter != null && _countingSorter.Valid)
{
    _countingSorter.Dispatch(cmd, (uint)EffectiveSplatCount,
        _gpuSortDistances, _gpuSortKeys);
}
else if (_sorter != null && _sorter.Valid)
{
    _sorterArgs.count = (uint)EffectiveSplatCount;
    _sorter.Dispatch(cmd, _sorterArgs);
}
```

**Step 4:** Dispose counting sort in `DisposeResourcesForAsset` (add after line 595):
```csharp
_countingSorter?.Dispose();
_countingSorter = null;
```

**Step 5:** Commit

---

## Task 5: Integrate Counting Sort into Global Sort Path

**Files:**
- Modify: `Assets/4DGS/Runtime/GaussianSplatRenderSystem.cs`

**Step 1:** Add counting sort field (after line 29)

```csharp
private GpuCountingSort _globalCountingSorter;
private ComputeShader _globalCountingSortShader;
```

**Step 2:** Add `EnsureGlobalCountingSorter` method (after `EnsureGlobalSorter`)

```csharp
private bool EnsureGlobalCountingSorter()
{
    var cs = _config?.CsCountingSort;
    if (cs == null) return false;
    if (_globalCountingSorter == null || _globalCountingSortShader != cs)
    {
        _globalCountingSorter?.Dispose();
        _globalCountingSorter = new GpuCountingSort(cs);
        _globalCountingSortShader = cs;
    }
    return _globalCountingSorter != null && _globalCountingSorter.Valid;
}
```

**Step 3:** Use counting sort in `SortAndRenderSplatsGlobal` (modify ~line 616-621)

Replace:
```csharp
if (!usedTile && (groupSortNeeded || !groupCache.hasSortedKeys))
{
    groupCache.sorterArgs.count = (uint)dstOffset;
    _globalSorter.Dispatch(cmb, groupCache.sorterArgs);
    groupCache.hasSortedKeys = true;
}
```

With:
```csharp
if (!usedTile && (groupSortNeeded || !groupCache.hasSortedKeys))
{
    if (EnsureGlobalCountingSorter())
    {
        _globalCountingSorter.Dispatch(cmb, (uint)dstOffset,
            groupCache.sortDistances, groupCache.sortKeys);
    }
    else
    {
        groupCache.sorterArgs.count = (uint)dstOffset;
        _globalSorter.Dispatch(cmb, groupCache.sorterArgs);
    }
    groupCache.hasSortedKeys = true;
}
```

**Step 4:** Dispose in `DisposeGlobalResources` (add after line 190):
```csharp
_globalCountingSorter?.Dispose();
_globalCountingSorter = null;
_globalCountingSortShader = null;
```

**Step 5:** Commit

---

## Task 6: Write Clean Compute Shader (Final Version)

The Task 1 prefix sum kernel had complexity issues. Here is the clean final version of `SplatCountingSort.compute`:

**Files:**
- Rewrite: `Assets/4DGS/Resources/SplatCountingSort.compute`

```hlsl
// GPU Counting Sort for Gaussian Splatting depth ordering.
// Quantizes 32-bit sortable distance keys into 4096 buckets, then
// counting-sorts splat indices by bucket in 4 lightweight dispatches.
// Replaces the wave-ops-dependent 4-pass radix sort.
//
// Pipeline:
//   1. CSCountingSortClear      — zero histogram
//   2. CSCountingSortCount      — build per-bucket counts (global atomics)
//   3. CSCountingSortPrefixSum  — exclusive scan → bucket start offsets
//   4. CSCountingSortScatter    — atomic scatter splat indices to sorted output

#if defined(SHADER_API_METAL)
#define GROUP_SIZE 256
#else
#define GROUP_SIZE 1024
#endif

#define NUM_BUCKETS 4096u
#define BUCKET_SHIFT 20u // top 12 bits of 32-bit key → 4096 buckets

#pragma kernel CSCountingSortClear
#pragma kernel CSCountingSortCount
#pragma kernel CSCountingSortPrefixSum
#pragma kernel CSCountingSortScatter

RWStructuredBuffer<uint> _Histogram;
StructuredBuffer<uint> _SortInput;
RWStructuredBuffer<uint> _SortOutput;
uint _SplatCount;

// ─── Kernel 1: Clear histogram ──────────────────────────────────────────
[numthreads(GROUP_SIZE, 1, 1)]
void CSCountingSortClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x < NUM_BUCKETS)
        _Histogram[id.x] = 0;
}

// ─── Kernel 2: Build histogram ──────────────────────────────────────────
[numthreads(GROUP_SIZE, 1, 1)]
void CSCountingSortCount(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _SplatCount)
        return;
    uint bucket = _SortInput[id.x] >> BUCKET_SHIFT;
    InterlockedAdd(_Histogram[bucket], 1);
}

// ─── Kernel 3: Exclusive prefix sum ─────────────────────────────────────
// Single-group dispatch. 256 threads scan 4096 elements (16 per thread).
// Uses 16 KB shared memory (within Apple GPU 32 KB limit).
groupshared uint gs_data[NUM_BUCKETS];
groupshared uint gs_blockSums[GROUP_SIZE];

[numthreads(GROUP_SIZE, 1, 1)]
void CSCountingSortPrefixSum(uint3 gtid : SV_GroupThreadID)
{
    uint tid = gtid.x;
    uint elemsPerThread = NUM_BUCKETS / GROUP_SIZE; // 16

    // Load histogram → shared memory
    uint base = tid * elemsPerThread;
    for (uint i = 0; i < elemsPerThread; i++)
        gs_data[base + i] = _Histogram[base + i];
    GroupMemoryBarrierWithGroupSync();

    // Per-thread sequential exclusive prefix sum
    uint threadSum = 0;
    for (uint i = 0; i < elemsPerThread; i++)
    {
        uint val = gs_data[base + i];
        gs_data[base + i] = threadSum;
        threadSum += val;
    }
    gs_blockSums[tid] = threadSum;
    GroupMemoryBarrierWithGroupSync();

    // Thread 0 scans the 256 block sums (sequential, O(256) — trivial)
    if (tid == 0)
    {
        uint running = 0;
        for (uint t = 0; t < GROUP_SIZE; t++)
        {
            uint val = gs_blockSums[t];
            gs_blockSums[t] = running;
            running += val;
        }
    }
    GroupMemoryBarrierWithGroupSync();

    // Add block offset to each element
    uint blockOffset = gs_blockSums[tid];
    for (uint i = 0; i < elemsPerThread; i++)
        gs_data[base + i] += blockOffset;
    GroupMemoryBarrierWithGroupSync();

    // Write back to histogram (now contains start offsets)
    for (uint i = 0; i < elemsPerThread; i++)
        _Histogram[base + i] = gs_data[base + i];
}

// ─── Kernel 4: Scatter ──────────────────────────────────────────────────
[numthreads(GROUP_SIZE, 1, 1)]
void CSCountingSortScatter(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _SplatCount)
        return;
    uint bucket = _SortInput[id.x] >> BUCKET_SHIFT;
    uint pos;
    InterlockedAdd(_Histogram[bucket], 1, pos);
    _SortOutput[pos] = id.x;
}
```

**Step 1:** Write the file with the content above

**Step 2:** Commit

---

## Task 7: Verify and Profile

**Step 1:** Open Unity, verify no compile errors

**Step 2:** Check the `GaussianSplatConfig` component in scene — `CsCountingSort` should auto-load from Resources

**Step 3:** Enter Play mode, verify Gaussian Splatting renders correctly (no visual artifacts beyond minor within-bucket ordering)

**Step 4:** Compare frame timing:
- Before: ~18 FPS (55 ms/frame)
- Expected: sorting overhead reduced from ~14 dispatches to 4 dispatches

**Step 5:** If visual quality is insufficient (visible popping at depth boundaries), increase `NUM_BUCKETS` to 8192 and `BUCKET_SHIFT` to 19. This requires changing the prefix sum kernel to handle 8192 elements (32 per thread, 32 KB shared memory — at Metal limit).

**Step 6:** Commit with final tuned values

---

## Implementation Notes

- **Bucket precision**: 4096 buckets with top-12-bit quantization gives ~2048 effective depth levels for visible splats (positive Z). At 100m range, each bucket ≈ 5 cm. Within-bucket ordering is arbitrary.
- **Fallback**: If `SplatCountingSort.compute` fails to compile on a platform, the existing `GpuSorting` (radix sort) is used automatically.
- **No wave ops**: The counting sort uses only `InterlockedAdd` and `GroupMemoryBarrierWithGroupSync` — no wave intrinsics required. This makes it universally compatible.
- **Buffer reuse**: The counting sort writes directly to `_SplatSortKeys`, reusing the existing buffer. No additional per-splat buffers needed. Only a 16 KB histogram buffer is added.
- **Global sort path**: The render system's global sort path (multiple renderers merged) also uses counting sort via the same `GpuCountingSort` class.
