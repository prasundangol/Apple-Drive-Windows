using System.Runtime.InteropServices;

namespace AppleDrive.Infrastructure.Iphone.Wpd.Interop;

/// <summary>Native PROPERTYKEY.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

/// <summary>
/// Native PROPVARIANT, 64-bit layout only (the app ships for x64 and ARM64).
/// Only the scalar members the importer reads are exposed.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    public const ushort VtEmpty = 0;
    public const ushort VtDate = 7;
    public const ushort VtFileTime = 64;

    [FieldOffset(0)]
    public ushort VariantType;

    [FieldOffset(8)]
    public double DoubleValue;

    [FieldOffset(8)]
    public long Int64Value;
}
