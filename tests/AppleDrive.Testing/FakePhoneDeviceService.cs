using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;

namespace AppleDrive.Testing;

/// <summary>Device detection double; tests attach/detach phones and raise change events.</summary>
public sealed class FakePhoneDeviceService : IPhoneDeviceService
{
    public static DeviceInfo TestPhone { get; } = new(@"\\?\usb#vid_05ac&pid_12a8#test", "Test iPhone", "Apple Inc.");

    public List<DeviceInfo> Devices { get; } = [];

    public bool IsWatching { get; private set; }

    public event EventHandler? DevicesChanged;

    public void StartWatching() => IsWatching = true;

    public Task<IReadOnlyList<DeviceInfo>> GetConnectedDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceInfo>>([.. Devices]);

    public void Attach(DeviceInfo? device = null)
    {
        Devices.Add(device ?? TestPhone);
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Detach()
    {
        Devices.Clear();
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
    }
}
