using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>What the user sees about the phone connection.</summary>
public enum DeviceState
{
    Searching,
    NoDevice,
    NeedsUnlockOrTrust,
    Unavailable,
    Connected,
}

/// <summary>Tracks whether an iPhone is attached and readable, and keeps the connection open.</summary>
public sealed partial class DeviceStatusViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan UnlockPollInterval = TimeSpan.FromSeconds(3);

    private readonly IPhoneDeviceService _deviceService;
    private readonly IPhonePhotoSource _photoSource;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<DeviceStatusViewModel> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CancellationTokenSource? _unlockPoll;
    private bool _activated;

    public DeviceStatusViewModel(
        IPhoneDeviceService deviceService,
        IPhonePhotoSource photoSource,
        IUiDispatcher dispatcher,
        ILogger<DeviceStatusViewModel> logger)
    {
        _deviceService = deviceService;
        _photoSource = photoSource;
        _dispatcher = dispatcher;
        _logger = logger;
        ApplyState(DeviceState.Searching, null, null);
    }

    /// <summary>Raised on the UI thread when <see cref="IsConnected"/> changes.</summary>
    public event EventHandler? ConnectionChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(IsNotConnected))]
    public partial DeviceState State { get; private set; }

    public bool IsSearching => State == DeviceState.Searching;

    /// <summary>True when the user may need to act (plug in, unlock, retry).</summary>
    public bool IsNotConnected => State is not (DeviceState.Connected or DeviceState.Searching);

    [ObservableProperty]
    public partial string Title { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceName { get; private set; } = string.Empty;

    /// <summary>Diagnostic text for "Show details"; empty when there is nothing to show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetails))]
    public partial string Details { get; private set; } = string.Empty;

    public bool HasDetails => Details.Length > 0;

    public bool IsConnected => State == DeviceState.Connected;

    /// <summary>Starts watching for devices and performs the first detection. Idempotent.</summary>
    public void Activate()
    {
        if (_activated)
        {
            return;
        }

        _activated = true;
        _deviceService.DevicesChanged += OnDevicesChanged;
        _deviceService.StartWatching();
        RefreshCommand.Execute(null);
    }

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            await RefreshCoreAsync(cancellationToken);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        var devices = await _deviceService.GetConnectedDevicesAsync(cancellationToken);
        if (devices.Count == 0)
        {
            await _photoSource.DisconnectAsync();
            ApplyState(DeviceState.NoDevice, null, null);
            return;
        }

        // Keep an existing connection (a scan may be using it) as long as that phone is still attached.
        if (_photoSource.ConnectedDevice is { } current && devices.Any(device => device.Id == current.Id))
        {
            ApplyState(DeviceState.Connected, current, null);
            return;
        }

        if (State is not DeviceState.NeedsUnlockOrTrust)
        {
            ApplyState(DeviceState.Searching, null, null);
        }

        var result = await _photoSource.ConnectAsync(devices[0], cancellationToken);
        var state = result.Status switch
        {
            DeviceConnectionStatus.Connected => DeviceState.Connected,
            DeviceConnectionStatus.NeedsUnlockOrTrust => DeviceState.NeedsUnlockOrTrust,
            DeviceConnectionStatus.NotFound => DeviceState.NoDevice,
            _ => DeviceState.Unavailable,
        };
        ApplyState(state, result.Device, result.Error);
    }

    private void ApplyState(DeviceState state, DeviceInfo? device, AppError? error)
    {
        var wasConnected = IsConnected;
        State = state;
        DeviceName = device?.FriendlyName ?? string.Empty;
        Details = error?.ToString() ?? string.Empty;
        (Title, Message) = state switch
        {
            DeviceState.Searching => (Strings.DeviceSearchingTitle, string.Empty),
            DeviceState.NoDevice => (Strings.DeviceNoneTitle, Strings.DeviceNoneBody),
            DeviceState.NeedsUnlockOrTrust => (Strings.DeviceLockedTitle, Strings.DeviceLockedBody),
            DeviceState.Unavailable => (Strings.DeviceUnavailableTitle, Strings.DeviceUnavailableBody),
            _ => (Strings.DeviceConnectedTitle, string.Empty),
        };

        UpdateUnlockPolling();
        if (wasConnected != IsConnected)
        {
            OnPropertyChanged(nameof(IsConnected));
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Unlocking the phone or tapping Trust raises no device event, so while waiting for the
    /// user we re-check periodically.
    /// </summary>
    private void UpdateUnlockPolling()
    {
        if (State != DeviceState.NeedsUnlockOrTrust)
        {
            _unlockPoll?.Cancel();
            _unlockPoll = null;
            return;
        }

        if (_unlockPoll is not null)
        {
            return;
        }

        _unlockPoll = new CancellationTokenSource();
        var token = _unlockPoll.Token;
        _ = PollForUnlockAsync(token);
    }

    private async Task PollForUnlockAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(UnlockPollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var tick = new TaskCompletionSource();
                _dispatcher.Post(async () =>
                {
                    try
                    {
                        await RefreshCommand.ExecuteAsync(null);
                    }
                    finally
                    {
                        tick.TrySetResult();
                    }
                });
                await tick.Task;
            }
        }
        catch (OperationCanceledException)
        {
            // Polling stops once the phone is readable or detached.
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) =>
        _dispatcher.Post(() =>
        {
            _logger.LogDebug("Device change notification; refreshing");
            RefreshCommand.Execute(null);
        });

    public void Dispose()
    {
        _deviceService.DevicesChanged -= OnDevicesChanged;
        _unlockPoll?.Cancel();
        _refreshLock.Dispose();
    }
}
