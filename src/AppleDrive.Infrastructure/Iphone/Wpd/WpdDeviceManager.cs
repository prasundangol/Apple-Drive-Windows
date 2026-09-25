using AppleDrive.Domain.Entities;
using AppleDrive.Infrastructure.Iphone.Wpd.Interop;

namespace AppleDrive.Infrastructure.Iphone.Wpd;

/// <summary>Lists WPD devices through <c>IPortableDeviceManager</c>.</summary>
internal static class WpdDeviceManager
{
    private const string AppleVendorId = "vid_05ac";

    /// <summary>Returns every WPD device, Apple or not. Must not be called on a UI (STA) thread.</summary>
    public static unsafe IReadOnlyList<DeviceInfo> GetAllDevices()
    {
        var manager = ComFactory.Create<IPortableDeviceManager>(WpdConstants.ClsidPortableDeviceManager);
        try
        {
            manager.RefreshDeviceList();

            uint count = 0;
            ThrowIfFailed(manager.GetDevices(null, ref count), "GetDevices");
            if (count == 0)
            {
                return [];
            }

            var ids = new nint[count];
            fixed (nint* idsPointer = ids)
            {
                ThrowIfFailed(manager.GetDevices(idsPointer, ref count), "GetDevices");
            }

            var devices = new List<DeviceInfo>((int)count);
            for (var index = 0; index < count; index++)
            {
                var id = ComFactory.TakeString(ids[index]);
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                devices.Add(new DeviceInfo(
                    id,
                    ReadDeviceString(manager, id, DeviceString.FriendlyName) ?? ReadDeviceString(manager, id, DeviceString.Description) ?? "Portable device",
                    ReadDeviceString(manager, id, DeviceString.Manufacturer) ?? string.Empty));
            }

            return devices;
        }
        finally
        {
            ComFactory.Release(manager);
        }
    }

    /// <summary>True when the device is an Apple phone/tablet.</summary>
    public static bool IsAppleDevice(DeviceInfo device) =>
        device.Id.Contains(AppleVendorId, StringComparison.OrdinalIgnoreCase)
        || device.Manufacturer.Contains("Apple", StringComparison.OrdinalIgnoreCase);

    private enum DeviceString
    {
        FriendlyName,
        Description,
        Manufacturer,
    }

    private static unsafe string? ReadDeviceString(IPortableDeviceManager manager, string id, DeviceString which)
    {
        uint length = 0;
        var hr = Call(manager, id, which, null, ref length);
        if (WpdErrors.Failed(hr) || length == 0)
        {
            return null;
        }

        var buffer = new char[length];
        fixed (char* bufferPointer = buffer)
        {
            hr = Call(manager, id, which, bufferPointer, ref length);
        }

        if (WpdErrors.Failed(hr))
        {
            return null;
        }

        var value = new string(buffer).TrimEnd('\0').Trim();
        return value.Length == 0 ? null : value;
    }

    private static unsafe int Call(IPortableDeviceManager manager, string id, DeviceString which, char* buffer, ref uint length) =>
        which switch
        {
            DeviceString.FriendlyName => manager.GetDeviceFriendlyName(id, buffer, ref length),
            DeviceString.Description => manager.GetDeviceDescription(id, buffer, ref length),
            _ => manager.GetDeviceManufacturer(id, buffer, ref length),
        };

    private static void ThrowIfFailed(int hr, string operation)
    {
        if (WpdErrors.Failed(hr))
        {
            throw new WpdException(WpdErrors.FromHResult(hr, operation));
        }
    }
}
