using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AppleDrive.Infrastructure.Iphone.Wpd.Interop;

/// <summary>Creates and releases COM objects through source-generated COM wrappers.</summary>
internal static partial class ComFactory
{
    private const uint ClsctxInprocServer = 0x1;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Creates an in-process COM object and returns it as <typeparamref name="T"/>.</summary>
    public static T Create<T>(Guid classId)
        where T : class
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("Apple Drive requires a 64-bit process.");
        }

        var interfaceId = typeof(T).GUID;
        var hr = CoCreateInstance(in classId, 0, ClsctxInprocServer, in interfaceId, out var pointer);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>Releases the native object now instead of waiting for garbage collection.</summary>
    public static void Release(object? comObject)
    {
        if (comObject is ComObject wrapper)
        {
            wrapper.FinalRelease();
        }
    }

    /// <summary>Converts a CoTaskMem-allocated UTF-16 string to managed and frees it.</summary>
    public static string? TakeString(nint pointer)
    {
        if (pointer == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid classId,
        nint outer,
        uint context,
        in Guid interfaceId,
        out nint pointer);

    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(ref PropVariant value);
}
