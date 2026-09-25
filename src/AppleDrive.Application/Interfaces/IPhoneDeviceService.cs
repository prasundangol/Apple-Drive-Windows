using AppleDrive.Domain.Entities;

namespace AppleDrive.Application.Interfaces;

/// <summary>Detects phones attached to the computer and reports arrival/removal.</summary>
public interface IPhoneDeviceService : IDisposable
{
    /// <summary>Raised (on a background thread) when a phone is attached or removed.</summary>
    event EventHandler? DevicesChanged;

    /// <summary>Starts listening for device arrival/removal. Safe to call more than once.</summary>
    void StartWatching();

    /// <summary>Returns the supported phones currently attached.</summary>
    Task<IReadOnlyList<DeviceInfo>> GetConnectedDevicesAsync(CancellationToken cancellationToken);
}
