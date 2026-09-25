using AppleDrive.Application.Services;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.ViewModels;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.UnitTests.Presentation;

public sealed class DashboardViewModelTests
{
    private readonly FakePhoneDeviceService _devices = new();
    private readonly FakeIPhonePhotoSource _source = new();
    private readonly ImportSession _session = new();
    private readonly DeviceStatusViewModel _device;
    private readonly PhoneScanViewModel _scan;

    public DashboardViewModelTests()
    {
        _device = new DeviceStatusViewModel(_devices, _source, new InlineUiDispatcher(), NullLogger<DeviceStatusViewModel>.Instance);
        _scan = new PhoneScanViewModel(new PhoneScanService(_source, NullLogger<PhoneScanService>.Instance), _session, _device);
    }

    [Fact]
    public async Task Reports_no_device_when_nothing_is_attached()
    {
        await _device.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(DeviceState.NoDevice, _device.State);
        Assert.Equal(Strings.DeviceNoneTitle, _device.Title);
        Assert.False(_scan.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Connects_to_an_attached_phone_and_enables_scanning()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);

        await _device.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(DeviceState.Connected, _device.State);
        Assert.Equal("Test iPhone", _device.DeviceName);
        Assert.True(_scan.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Locked_phone_asks_user_to_unlock()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);
        _source.ConnectStatus = DeviceConnectionStatus.NeedsUnlockOrTrust;

        await _device.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(DeviceState.NeedsUnlockOrTrust, _device.State);
        Assert.Equal(Strings.DeviceLockedTitle, _device.Title);
        Assert.True(_device.HasDetails);
        Assert.False(_scan.ScanCommand.CanExecute(null));
        _device.Dispose();
    }

    [Fact]
    public async Task Refresh_keeps_an_existing_connection()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);
        await _device.RefreshCommand.ExecuteAsync(null);

        await _device.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(1, _source.ConnectCount);
    }

    [Fact]
    public async Task Detaching_the_phone_disconnects()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);
        await _device.RefreshCommand.ExecuteAsync(null);

        _devices.Devices.Clear();
        await _device.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(DeviceState.NoDevice, _device.State);
        Assert.Null(_source.ConnectedDevice);
        Assert.False(_scan.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Scan_shows_counts_and_stores_result_in_session()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);
        _source.AddFile("Internal Storage/202409__/IMG_0001.HEIC", new byte[1000]);
        _source.AddFile("Internal Storage/202409__/IMG_0001.MOV", new byte[3000]);
        _source.AddFile("Internal Storage/202409__/IMG_0002.JPG", new byte[500]);
        _source.AddFile("Internal Storage/202409__/IMG_0003.MOV", new byte[9000]);
        await _device.RefreshCommand.ExecuteAsync(null);

        await _scan.ScanCommand.ExecuteAsync(null);

        Assert.True(_scan.HasMedia);
        Assert.Equal(2, _scan.PhotoCount);
        Assert.Equal(1, _scan.LivePhotoCount);
        Assert.Equal(1, _scan.VideoCount);
        Assert.Equal(4, _scan.FileCount);
        Assert.NotNull(_session.LastPhoneScan);
        Assert.False(_scan.IsScanning);
    }

    [Fact]
    public async Task Empty_phone_shows_empty_state()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);
        await _device.RefreshCommand.ExecuteAsync(null);

        await _scan.ScanCommand.ExecuteAsync(null);

        Assert.True(_scan.IsEmpty);
        Assert.False(_scan.HasMedia);
    }

    [Fact]
    public async Task Scan_failure_shows_friendly_message_with_details()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);
        await _device.RefreshCommand.ExecuteAsync(null);
        _source.EnumerationError = new AppError(ErrorKind.DeviceDisconnected, "gone", unchecked((int)0x8007048F));

        await _scan.ScanCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ErrorDeviceDisconnected, _scan.ErrorMessage);
        Assert.Contains("8007048F", _scan.ErrorDetails);
        Assert.False(_scan.HasResult);
    }

    [Fact]
    public async Task Scan_can_be_cancelled()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);
        for (var index = 0; index < 50; index++)
        {
            _source.AddFile($"Internal Storage/a/IMG_{index:0000}.JPG", new byte[10]);
        }

        _source.EnumerationDelayPerFile = TimeSpan.FromMilliseconds(20);
        await _device.RefreshCommand.ExecuteAsync(null);

        var scan = _scan.ScanCommand.ExecuteAsync(null);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _scan.ScanCancelCommand.Execute(null);
        await scan;

        Assert.Equal(Strings.ScanCancelled, _scan.ErrorMessage);
        Assert.Empty(_scan.ErrorDetails);
        Assert.False(_scan.IsScanning);
        Assert.Null(_session.LastPhoneScan);
    }
}
