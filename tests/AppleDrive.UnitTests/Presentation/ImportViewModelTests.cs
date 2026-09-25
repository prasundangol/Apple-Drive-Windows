using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Hashing;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.ViewModels;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.UnitTests.Presentation;

public sealed class ImportViewModelTests : IDisposable
{
    private readonly TemporaryDirectory _destination = new();
    private readonly FakePhoneDeviceService _devices = new();
    private readonly FakeIPhonePhotoSource _phone = new();
    private readonly InMemoryMediaRepository _repository = new();
    private readonly ImportSession _session = new();
    private readonly DeviceStatusViewModel _device;

    public ImportViewModelTests() =>
        _device = new DeviceStatusViewModel(_devices, _phone, new InlineUiDispatcher(), NullLogger<DeviceStatusViewModel>.Instance);

    [Fact]
    public void Asks_for_a_phone_first()
    {
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Path });

        Assert.Equal(Strings.ImportNeedsDevice, viewModel.PrerequisiteMessage);
        Assert.False(viewModel.AnalyzeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Asks_for_a_destination_when_the_phone_is_connected()
    {
        await ConnectPhoneAsync();

        var viewModel = Create(new AppSettings());

        Assert.Equal(Strings.ImportNeedsDestination, viewModel.PrerequisiteMessage);
        Assert.False(viewModel.AnalyzeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Analysis_shows_new_and_duplicate_counts()
    {
        var shared = new byte[1_000];
        shared[0] = 1;
        File.WriteAllBytes(_destination.Combine("existing.jpg"), shared);
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", shared);
        _phone.AddFile("Internal Storage/a/IMG_2.JPG", new byte[2_000]);
        await ConnectPhoneAsync();
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Path });

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasError, viewModel.ErrorDetails);
        Assert.True(viewModel.HasPlan);
        Assert.Equal("2", viewModel.TotalText);
        Assert.Equal("1", viewModel.NewText);
        Assert.Equal("1", viewModel.DuplicateText);
        Assert.False(viewModel.HasNothingNew);
        Assert.Same(viewModel.Plan, _session.Plan);
    }

    [Fact]
    public async Task Shows_nothing_new_when_everything_already_exists()
    {
        var shared = new byte[1_000];
        shared[0] = 2;
        File.WriteAllBytes(_destination.Combine("existing.jpg"), shared);
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", shared);
        await ConnectPhoneAsync();
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Path });

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.True(viewModel.HasNothingNew);
    }

    [Fact]
    public async Task Unplugged_destination_shows_a_friendly_error()
    {
        await ConnectPhoneAsync();
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Combine("missing-drive") });

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ErrorDestinationUnavailable, viewModel.ErrorMessage);
        Assert.False(viewModel.HasPlan);
    }

    private async Task ConnectPhoneAsync()
    {
        _devices.Devices.Add(FakePhoneDeviceService.TestPhone);
        await _device.RefreshCommand.ExecuteAsync(null);
    }

    private ImportViewModel Create(AppSettings settings)
    {
        var phoneScan = new PhoneScanService(_phone, NullLogger<PhoneScanService>.Instance);
        var index = new DestinationIndexService(
            new DestinationScanner(NullLogger<DestinationScanner>.Instance), _repository, TimeProvider.System, NullLogger<DestinationIndexService>.Instance);
        var detector = new ExactDuplicateDetector(_phone, _repository, new Sha256HashService(), NullLogger<ExactDuplicateDetector>.Instance);
        var analysis = new ImportAnalysisService(phoneScan, index, detector, _session, NullLogger<ImportAnalysisService>.Instance);
        return new ImportViewModel(analysis, _session, new FakeSettingsService(settings), _device, new InlineUiDispatcher());
    }

    public void Dispose() => _destination.Dispose();
}
