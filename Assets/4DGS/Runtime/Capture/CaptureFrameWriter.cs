using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GaussianSplatting.Runtime
{
    // Gets frame encoding off the main thread.
    //
    // The synchronous path — ReadPixels, EncodeToPNG, WriteAllBytes — costs
    // seconds per frame at 8K, all of it on the thread that also runs the editor
    // UI, which is why the editor appears to hang during a capture. Here the GPU
    // readback is asynchronous and the PNG encode runs on the thread pool; the
    // main thread is left with one memcpy per view.
    //
    // Buffers are pooled rather than allocated per frame: at 8K a single frame is
    // ~268 MB of RGBA plus ~192 MB of RGB scratch, and churning that through the
    // large object heap would cost more than it saves. The pool size therefore
    // doubles as the in-flight cap — capture throttles itself to whatever the
    // encoder can keep up with instead of growing memory without bound.
    internal sealed class CaptureFrameWriter : IDisposable
    {
        private sealed class Slot
        {
            public NativeArray<byte> Raw;    // readback destination, frame-sized
            public byte[] Rgb;               // PNG repack scratch; null for EXR
            public int FrameIndex;
            public int ViewsRemaining;
            public string Path;
        }

        private readonly ConcurrentQueue<Slot> _free = new ConcurrentQueue<Slot>();
        private readonly Slot[] _slots;
        private readonly int _frameWidth;
        private readonly int _frameHeight;
        private readonly CaptureOutputFormat _format;
        private readonly int _bytesPerPixel;
        private int _inFlight;
        private int _failures;

        public int InFlight => Volatile.Read(ref _inFlight);
        public int Capacity => _slots.Length;
        public int Failures => Volatile.Read(ref _failures);

        // Bytes per pixel of the readback: RGBA32 for PNG, RGBA half for EXR.
        public static int BytesPerPixelFor(CaptureOutputFormat format) =>
            format == CaptureOutputFormat.Exr ? 8 : 4;

        public CaptureFrameWriter(int frameWidth, int frameHeight, int capacity, CaptureOutputFormat format)
        {
            _frameWidth = frameWidth;
            _frameHeight = frameHeight;
            _format = format;
            _bytesPerPixel = BytesPerPixelFor(format);
            _slots = new Slot[Mathf.Max(1, capacity)];
            int pixels = frameWidth * frameHeight;
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = new Slot
                {
                    Raw = new NativeArray<byte>(pixels * _bytesPerPixel, Allocator.Persistent,
                                                NativeArrayOptions.ClearMemory),
                    // EXR encodes straight from the readback buffer; only the PNG
                    // path needs somewhere to drop the alpha channel.
                    Rgb = format == CaptureOutputFormat.Png ? new byte[pixels * 3] : null,
                };
                _free.Enqueue(_slots[i]);
            }
        }

        public bool HasFreeSlot => !_free.IsEmpty;

        // Blocks until a buffer frees up. Safe only once every in-flight slot has
        // had all its views submitted — slots are released by encoder threads, but
        // reaching that state depends on readback callbacks, which are dispatched
        // on the main thread. Callers must flush readbacks first.
        public void WaitForFreeSlot(float timeoutSeconds = 120f)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (_free.IsEmpty && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(1);
            }
        }

        // Null when every buffer is still in flight; the caller should wait a
        // frame rather than allocate, so memory stays bounded.
        public object TryBeginFrame(int frameIndex, string path, int viewCount)
        {
            if (!_free.TryDequeue(out Slot slot))
            {
                return null;
            }
            slot.FrameIndex = frameIndex;
            slot.Path = path;
            slot.ViewsRemaining = viewCount;
            Interlocked.Increment(ref _inFlight);
            return slot;
        }

        // Copies one view's readback into the frame buffer with its lower left
        // corner at (x, y). Row 0 is the bottom row, matching what ReadPixels
        // produces, so the encoder's vertical flip puts higher rows on top.
        public void SubmitView(object handle, NativeArray<byte> src, int viewWidth, int viewHeight, int x, int y)
        {
            var slot = (Slot)handle;
            int viewRowBytes = viewWidth * _bytesPerPixel;
            int frameRowBytes = _frameWidth * _bytesPerPixel;

            if (viewWidth == _frameWidth)
            {
                NativeArray<byte>.Copy(src, 0, slot.Raw, y * frameRowBytes, viewRowBytes * viewHeight);
            }
            else
            {
                int xOffsetBytes = x * _bytesPerPixel;
                for (int row = 0; row < viewHeight; row++)
                {
                    NativeArray<byte>.Copy(src, row * viewRowBytes, slot.Raw,
                        (y + row) * frameRowBytes + xOffsetBytes, viewRowBytes);
                }
            }

            if (--slot.ViewsRemaining > 0)
            {
                return;
            }
            DispatchEncode(slot);
        }

        // Called when a readback fails; the slot must go back or the pool drains.
        public void AbandonFrame(object handle)
        {
            var slot = (Slot)handle;
            Interlocked.Increment(ref _failures);
            Release(slot);
        }

        private void DispatchEncode(Slot slot)
        {
            NativeArray<byte> raw = slot.Raw;
            byte[] rgb = slot.Rgb;
            string path = slot.Path;
            int width = _frameWidth;
            int height = _frameHeight;
            CaptureOutputFormat format = _format;

            Task.Run(() =>
            {
                try
                {
                    if (format == CaptureOutputFormat.Exr)
                    {
                        // Encoded straight from the readback: alpha is the coverage
                        // matte an AOV needs, so there is nothing to strip.
                        NativeArray<byte> exr = ImageConversion.EncodeNativeArrayToEXR(
                            raw, GraphicsFormat.R16G16B16A16_SFloat, (uint)width, (uint)height,
                            0, Texture2D.EXRFlags.CompressZIP);
                        File.WriteAllBytes(path, exr.ToArray());
                        exr.Dispose();
                    }
                    else
                    {
                        // RGBA -> RGB. AsyncGPUReadback cannot convert to a 3-byte
                        // format, so the repack has to happen somewhere; doing it
                        // here keeps it off the main thread and off the GPU.
                        for (int i = 0, s = 0, d = 0; i < width * height; i++, s += 4, d += 3)
                        {
                            rgb[d] = raw[s];
                            rgb[d + 1] = raw[s + 1];
                            rgb[d + 2] = raw[s + 2];
                        }

                        byte[] png = ImageConversion.EncodeArrayToPNG(
                            rgb, GraphicsFormat.R8G8B8_UNorm, (uint)width, (uint)height);
                        File.WriteAllBytes(path, png);
                    }
                }
                catch (Exception e)
                {
                    Interlocked.Increment(ref _failures);
                    Debug.LogError($"[SequenceCapture] Encoding {path} failed: {e.Message}");
                }
                finally
                {
                    Release(slot);
                }
            });
        }

        private void Release(Slot slot)
        {
            Interlocked.Decrement(ref _inFlight);
            _free.Enqueue(slot);
        }

        // Blocks until every queued encode has finished. Only used at teardown,
        // where stalling briefly is preferable to disposing buffers a worker
        // thread is still reading.
        public void Drain(float timeoutSeconds = 120f)
        {
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (InFlight > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
            if (InFlight > 0)
            {
                Debug.LogWarning($"[SequenceCapture] {InFlight} frame(s) still encoding after {timeoutSeconds}s; " +
                                 "their output may be incomplete.");
            }
        }

        public void Dispose()
        {
            Drain();
            foreach (Slot slot in _slots)
            {
                if (slot.Raw.IsCreated)
                {
                    slot.Raw.Dispose();
                }
                slot.Rgb = null;
            }
        }
    }
}
