using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AppleDrive.Infrastructure.Iphone.Wpd.Interop;

// Minimal Windows Portable Devices (PortableDeviceApi.h) COM declarations.
//
// Only the leading vtable slots that the importer needs are declared; slots after the last
// declared method are never called, so omitting them is safe. Methods that would modify the
// device (Delete, Move, Copy, CreateObject*, CreateResource, SetValues) are deliberately
// not declared, which makes the importer read-only by construction.
//
// Vtable order was checked against the Win32 metadata projection of PortableDeviceApi.h.

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("A1567595-4C2F-4574-A6FA-ECEF917B9A40")]
internal unsafe partial interface IPortableDeviceManager
{
    [PreserveSig]
    int GetDevices(nint* pnpDeviceIds, ref uint count);

    [PreserveSig]
    int RefreshDeviceList();

    [PreserveSig]
    int GetDeviceFriendlyName(string pnpDeviceId, char* buffer, ref uint charCount);

    [PreserveSig]
    int GetDeviceDescription(string pnpDeviceId, char* buffer, ref uint charCount);

    [PreserveSig]
    int GetDeviceManufacturer(string pnpDeviceId, char* buffer, ref uint charCount);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("625E2DF8-6392-4CF0-9AD1-3CFA5F17775C")]
internal partial interface IPortableDevice
{
    [PreserveSig]
    int Open(string pnpDeviceId, IPortableDeviceValues clientInfo);

    [PreserveSig]
    int SendCommand(uint flags, nint parameters, out nint results);

    [PreserveSig]
    int Content(out IPortableDeviceContent content);

    [PreserveSig]
    int Capabilities(out nint capabilities);

    [PreserveSig]
    int Cancel();

    [PreserveSig]
    int Close();
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6A96ED84-7C73-4480-9938-BF5AF477D426")]
internal partial interface IPortableDeviceContent
{
    [PreserveSig]
    int EnumObjects(uint flags, string parentObjectId, nint filter, out IEnumPortableDeviceObjectIDs enumerator);

    [PreserveSig]
    int Properties(out IPortableDeviceProperties properties);

    [PreserveSig]
    int Transfer(out IPortableDeviceResources resources);
}

[GeneratedComInterface]
[Guid("10ECE955-CF41-4728-BFA0-41EEDF1BBF19")]
internal unsafe partial interface IEnumPortableDeviceObjectIDs
{
    /// <summary>Returns S_OK when <paramref name="count"/> ids were fetched, S_FALSE when fewer.</summary>
    [PreserveSig]
    int Next(uint count, nint* objectIds, out uint fetched);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("7F6D695C-03DF-4439-A809-59266BEEE3A6")]
internal partial interface IPortableDeviceProperties
{
    [PreserveSig]
    int GetSupportedProperties(string objectId, out nint keys);

    [PreserveSig]
    int GetPropertyAttributes(string objectId, in PropertyKey key, out nint attributes);

    /// <summary>Returns S_FALSE when some requested properties could not be read.</summary>
    [PreserveSig]
    int GetValues(string objectId, IPortableDeviceKeyCollection keys, out IPortableDeviceValues values);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("FD8878AC-D841-4D17-891C-E6829CDB6934")]
internal partial interface IPortableDeviceResources
{
    [PreserveSig]
    int GetSupportedResources(string objectId, out nint keys);

    [PreserveSig]
    int GetResourceAttributes(string objectId, in PropertyKey key, out nint attributes);

    [PreserveSig]
    int GetStream(string objectId, in PropertyKey key, uint mode, ref uint optimalBufferSize, out nint stream);
}

[GeneratedComInterface]
[Guid("DADA2357-E0AD-492E-98DB-DD61C53BA353")]
internal partial interface IPortableDeviceKeyCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PropertyKey key);

    [PreserveSig]
    int Add(in PropertyKey key);
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("6848F6F2-3155-4F86-B6F5-263EEEAB3143")]
internal partial interface IPortableDeviceValues
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, nint key, nint value);
    [PreserveSig] int SetValue(in PropertyKey key, in PropVariant value);
    [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
    [PreserveSig] int SetStringValue(in PropertyKey key, string value);
    [PreserveSig] int GetStringValue(in PropertyKey key, out nint value);
    [PreserveSig] int SetUnsignedIntegerValue(in PropertyKey key, uint value);
    [PreserveSig] int GetUnsignedIntegerValue(in PropertyKey key, out uint value);
    [PreserveSig] int SetSignedIntegerValue(in PropertyKey key, int value);
    [PreserveSig] int GetSignedIntegerValue(in PropertyKey key, out int value);
    [PreserveSig] int SetUnsignedLargeIntegerValue(in PropertyKey key, ulong value);
    [PreserveSig] int GetUnsignedLargeIntegerValue(in PropertyKey key, out ulong value);
    [PreserveSig] int SetSignedLargeIntegerValue(in PropertyKey key, long value);
    [PreserveSig] int GetSignedLargeIntegerValue(in PropertyKey key, out long value);
    [PreserveSig] int SetFloatValue(in PropertyKey key, float value);
    [PreserveSig] int GetFloatValue(in PropertyKey key, out float value);
    [PreserveSig] int SetErrorValue(in PropertyKey key, int value);
    [PreserveSig] int GetErrorValue(in PropertyKey key, out int value);
    [PreserveSig] int SetKeyValue(in PropertyKey key, in PropertyKey value);
    [PreserveSig] int GetKeyValue(in PropertyKey key, out PropertyKey value);
    [PreserveSig] int SetBoolValue(in PropertyKey key, int value);
    [PreserveSig] int GetBoolValue(in PropertyKey key, out int value);
    [PreserveSig] int SetIUnknownValue(in PropertyKey key, nint value);
    [PreserveSig] int GetIUnknownValue(in PropertyKey key, out nint value);
    [PreserveSig] int SetGuidValue(in PropertyKey key, in Guid value);
    [PreserveSig] int GetGuidValue(in PropertyKey key, out Guid value);
}
