// SPDX-License-Identifier: MIT

using System;
using System.Runtime.InteropServices;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace GaussianSplatting.Runtime
{
    // One kind of style block as the shaders read it: its data for every
    // style slot of a GaussianStyleSet, bound as the float4 rows the block's
    // HLSL file declares and unpacks.
    internal abstract class GaussianStyleChannel : IDisposable
    {
        // Loads the slot from the style's block of this kind; without one,
        // or without a style, the slot reads as absent
        public abstract void Write(int slot, GaussianSplatStyle style);

        // Binds the slots to a compute shader. active is false when no
        // style applies; what the kernels sample is bound all the same.
        public abstract void Bind(CommandBuffer cmb, ComputeShader cs, int kernelIndex, bool active);

        public virtual void Dispose()
        {
        }
    }

    internal class GaussianStyleChannel<TBlock, TData> : GaussianStyleChannel
        where TBlock : GaussianStyleBlock<TData>
        where TData : unmanaged
    {
        private readonly int _rowsId;
        private readonly int _rowsPerSlot;
        private readonly Vector4[] _rows;

        // The rows are named after the block's HLSL struct: StyleRim's are
        // _StyleRim
        public GaussianStyleChannel(string structName)
        {
            _rowsId = Shader.PropertyToID("_" + structName);
            _rowsPerSlot = UnsafeUtility.SizeOf<TData>() / UnsafeUtility.SizeOf<Vector4>();
            Debug.Assert(_rowsPerSlot * UnsafeUtility.SizeOf<Vector4>() == UnsafeUtility.SizeOf<TData>(),
                $"{typeof(TData).FullName} must be whole rows of four floats");
            _rows = new Vector4[GaussianStyleSet.MaxStyles * _rowsPerSlot];
        }

        public override void Write(int slot, GaussianSplatStyle style)
        {
            TData data = style != null && style.TryGet(out TBlock block) ? block.Pack(style) : default;
            MemoryMarshal.Cast<TData, Vector4>(MemoryMarshal.CreateSpan(ref data, 1))
                .CopyTo(_rows.AsSpan(slot * _rowsPerSlot, _rowsPerSlot));
        }

        public override void Bind(CommandBuffer cmb, ComputeShader cs, int kernelIndex, bool active)
        {
            cmb.SetComputeVectorArrayParam(cs, _rowsId, _rows);
        }
    }
}
