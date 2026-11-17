// A hashed grid over a set of points, rebuilt each frame, for finding the
// points near a position (GaussianParticleLinks, GaussianGridLinks). Cells
// are as wide as the farthest search, so a search looks at the 27 cells
// around its own. Built in four kernels: clear the cells, count the points
// per cell, offset the cells (one group), list the points cell by cell.
// GaussianPointHash (C#) owns the buffers and runs them.
//
// The includer defines, before including:
//   uint HashPointCount();          the points
//   bool HashPointActive(uint i);   whether a point is listed
//   float3 HashPointPos(uint i);    its world position
// and declares the kernels:
//   #pragma kernel CSHashClear
//   #pragma kernel CSHashCount
//   #pragma kernel CSHashScan
//   #pragma kernel CSHashScatter
#ifndef GAUSSIAN_POINT_HASH_HLSL
#define GAUSSIAN_POINT_HASH_HLSL

#define HASH_GROUP_SIZE 256
// Must match GaussianPointHash.ScanGroupSize
#define HASH_SCAN_GROUP_SIZE 1024
#define HASH_NO_CELL 0xFFFFFFFFu

uint _HashCellCount;        // a power of two, at least HASH_SCAN_GROUP_SIZE
float _HashCellSize;        // world units

// Written by one kernel and read by later ones, so declared twice: writable
// for the kernel that fills them, read-only (*Read, the same GraphicsBuffer)
// after

// Per cell: points in it, and where its run starts in the list
RWStructuredBuffer<uint> _HashCells;
StructuredBuffer<uint> _HashCellsRead;
RWStructuredBuffer<uint> _HashCellStarts;
StructuredBuffer<uint> _HashCellStartsRead;
// Per point: its cell (HASH_NO_CELL when not listed), and its place in it
RWStructuredBuffer<uint2> _HashPointCells;
StructuredBuffer<uint2> _HashPointCellsRead;
// The listed points, cell by cell
RWStructuredBuffer<uint> _HashSorted;
StructuredBuffer<uint> _HashSortedRead;

int3 HashCellCoord(float3 p)
{
    return (int3)floor(p / _HashCellSize);
}

uint HashCellIndex(int3 c)
{
    uint h = (uint)c.x * 73856093u ^ (uint)c.y * 19349663u ^ (uint)c.z * 83492791u;
    h ^= h >> 16;
    h *= 0x7feb352du;
    h ^= h >> 15;
    return h & (_HashCellCount - 1);
}

// The cell at this coordinate unless an earlier one of the visited cells
// hashed to it too, so a search lists each point once
bool HashVisitCell(int3 c, inout uint visited[27], inout uint visitedCount, out uint cell)
{
    cell = HashCellIndex(c);
    [loop]
    for (uint v = 0; v < visitedCount; v++)
    {
        if (visited[v] == cell)
            return false;
    }
    visited[visitedCount++] = cell;
    return true;
}

[numthreads(HASH_GROUP_SIZE, 1, 1)]
void CSHashClear(uint3 id : SV_DispatchThreadID)
{
    if (id.x < _HashCellCount)
        _HashCells[id.x] = 0;
}

[numthreads(HASH_GROUP_SIZE, 1, 1)]
void CSHashCount(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= HashPointCount())
        return;
    uint2 pointCell = uint2(HASH_NO_CELL, 0);
    if (HashPointActive(i))
    {
        uint cell = HashCellIndex(HashCellCoord(HashPointPos(i)));
        uint slot;
        InterlockedAdd(_HashCells[cell], 1, slot);
        pointCell = uint2(cell, slot);
    }
    _HashPointCells[i] = pointCell;
}

groupshared uint gs_HashScan[HASH_SCAN_GROUP_SIZE];

// Exclusive prefix sum of the cell counts, in one group: each thread sums
// a run of cells, the group scans the runs, each thread writes its run
[numthreads(HASH_SCAN_GROUP_SIZE, 1, 1)]
void CSHashScan(uint3 id : SV_GroupThreadID)
{
    uint t = id.x;
    uint run = _HashCellCount / HASH_SCAN_GROUP_SIZE;
    uint begin = t * run;
    uint sum = 0;
    for (uint c = 0; c < run; c++)
        sum += _HashCellsRead[begin + c];
    gs_HashScan[t] = sum;
    GroupMemoryBarrierWithGroupSync();
    [unroll]
    for (uint offset = 1; offset < HASH_SCAN_GROUP_SIZE; offset <<= 1)
    {
        uint add = t >= offset ? gs_HashScan[t - offset] : 0;
        GroupMemoryBarrierWithGroupSync();
        gs_HashScan[t] += add;
        GroupMemoryBarrierWithGroupSync();
    }
    uint start = gs_HashScan[t] - sum;
    for (uint c2 = 0; c2 < run; c2++)
    {
        _HashCellStarts[begin + c2] = start;
        start += _HashCellsRead[begin + c2];
    }
}

[numthreads(HASH_GROUP_SIZE, 1, 1)]
void CSHashScatter(uint3 id : SV_DispatchThreadID)
{
    uint i = id.x;
    if (i >= HashPointCount())
        return;
    uint2 pointCell = _HashPointCellsRead[i];
    if (pointCell.x != HASH_NO_CELL)
        _HashSorted[_HashCellStartsRead[pointCell.x] + pointCell.y] = i;
}

#endif // GAUSSIAN_POINT_HASH_HLSL
