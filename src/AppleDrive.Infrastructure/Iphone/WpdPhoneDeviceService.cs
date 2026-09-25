using System.Runtime.InteropServices;
using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Infrastructure.Iphone.Wpd;
using AppleDrive.Infrastructure.Iphone.Wpd.Interop;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;

namespace AppleDrive.Infrastructure.Iphone;

/// <summary>
/// Finds attached iPhones through the WPD device manager and watches the WPD device
/// interface class for arrival and removal.
/// </summary>
public sealed class WpdPhoneDeviceService(ILogger<WpdPhoneDeviceService> logger) : IPhoneDeviceService
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(750);

    private readonly Lock _gate = new();
    private DeviceWatcher? _watcher;
    private Timer? _debounceTimer;
    private bool _initialEnumerationDone;

    public event EventHandler? DevicesChanged;

    public void StartWatching()
    {
        lock (_gate)
        {
            if (_watcher is not null)
            {
                return;
            }

            var selector =
                $"System.Devices.InterfaceClassGuid:=\"{{{WpdConstants.WpdDeviceInterfaceClass}}}\" " +
                "AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";
            _watcher = DeviceInformation.CreateWatcher(selector, null, DeviceInformationKind.DeviceInterface);
            _watcher.Added += (_, _) => OnDeviceEvent();
            _watcher.Removed += (_, _) => OnDeviceEvent();
            _watcher.EnumerationCompleted += (_, _) => _initialEnumerationDone = true;
            _watcher.Start();
            logger.LogDebug("Device watcher started");
        }
    }

    public Task<IReadOnlyList<DeviceInfo>> GetConnectedDevicesAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DeviceInfo>>(
            () =>
            {
                try
                {
                    var all = WpdDeviceManager.GetAllDevices();
                    var apple = all.Where(WpdDeviceManager.IsAppleDevice).ToList();
                    logger.LogDebug("Found {Total} portable devices, {Apple} Apple", all.Count, apple.Count);
                    return apple;
                }
                catch (WpdException exception)
                {
                    logger.LogWarning("Could not list portable devices: {Error}", exception.Error);
                    return [];
                }
                catch (COMException exception)
                {
                    logger.LogWarning(exception, "Could not list portable devices");
                    return [];
                }
            },
            cancellationToken);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
            {
                _watcher.Stop();
            }

            _watcher = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    private void OnDeviceEvent()
    {
        // The watcher reports every existing device once at start-up; those are not changes.
        if (!_initialEnumerationDone)
        {
            return;
        }

        lock (_gate)
        {
            // A single plug-in produces several interface events; coalesce them.
            _debounceTimer ??= new Timer(_ => RaiseDevicesChanged());
            _debounceTimer.Change(ChangeDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseDevicesChanged()
    {
        logger.LogInformation("Portable device arrival/removal detected");
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }
}
