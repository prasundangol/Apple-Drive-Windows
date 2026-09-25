using System.Runtime.InteropServices;

namespace AppleDrive.Infrastructure.Iphone.Wpd.Interop;

/// <summary>
/// Raw, deterministically released IStream pointer used for device reads.
/// </summary>
/// <remarks>
/// The Apple WPD driver allows one open data stream per device. A stream kept alive by a
/// managed wrapper until garbage collection leaves the device reporting ERROR_BUSY, and in
/// testing a subsequent open then delivered the bytes of an earlier object. Calling Read
/// through the vtable and releasing the pointer ourselves guarantees the stream is closed
/// the moment the caller disposes it.
/// </remarks>
internal sealed unsafe class NativeComStream : IDisposable
{
    // IUnknown occupies slots 0-2; ISequentialStream::Read is slot 3 of IStream's vtable.
    private const int ReadSlot = 3;

    private nint _pointer;

    public NativeComStream(nint pointer)
    {
        if (pointer == 0)
        {
            throw new ArgumentNullException(nameof(pointer));
        }

        _pointer = pointer;
    }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes. Returns the HRESULT.</summary>
    public int Read(Span<byte> buffer, out uint read)
    {
        ObjectDisposedException.ThrowIf(_pointer == 0, this);
        var vtable = *(nint**)_pointer;
        var readFunction = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)vtable[ReadSlot];
        uint bytesRead = 0;
        int hr;
        fixed (byte* bufferPointer = buffer)
        {
            hr = readFunction(_pointer, bufferPointer, (uint)buffer.Length, &bytesRead);
        }

        read = bytesRead;
        return hr;
    }

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0)
        {
            Marshal.Release(pointer);
        }
    }
}
