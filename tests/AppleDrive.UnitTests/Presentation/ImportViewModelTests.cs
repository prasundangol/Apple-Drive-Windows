using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Hashing;
using AppleDrive.Infrastructure.Imaging;
using AppleDrive.Infrastructure.Metadata;
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
    private readonly InMemoryTransferRepository _history = new();
    private readonly FakeShellServices _shell = new();
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

    [Fact]
    public async Task Start_transfer_asks_first_and_copies_nothing_when_declined()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", new byte[1_500]);
        await ConnectPhoneAsync();
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Path, Organization = FolderOrganization.Flat });
        await viewModel.AnalyzeCommand.ExecuteAsync(null);
        _shell.ConfirmResult = false;

        await viewModel.Transfer.StartCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(_shell.Confirmations);
        Assert.Equal(Strings.ConfirmTransferTitle, confirmation.Title);
        Assert.Contains(confirmation.Details, detail => detail.Key == Strings.ConfirmNewLabel && detail.Value == "1");
        Assert.Contains(confirmation.Details, detail => detail.Key == Strings.DestinationLabel && detail.Value == _destination.Path);
        Assert.True(viewModel.Transfer.IsIdle);
        Assert.False(File.Exists(_destination.Combine("IMG_1.JPG")));
    }

    [Fact]
    public async Task Transfer_shows_a_summary_and_done_returns_to_the_start()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", new byte[1_500]);
        _phone.AddFile("Internal Storage/a/IMG_2.JPG", new byte[2_500]);
        await ConnectPhoneAsync();
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Path, Organization = FolderOrganization.Flat });
        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        await viewModel.Transfer.StartCommand.ExecuteAsync(null);

        Assert.True(viewModel.Transfer.IsFinished);
        Assert.False(viewModel.ShowPlan);
        Assert.False(viewModel.ShowIntro);
        Assert.Equal(Strings.TransferCompleteTitle, viewModel.Transfer.SummaryTitle);
        Assert.Equal("2", viewModel.Transfer.TransferredText);
        Assert.Equal("0", viewModel.Transfer.FailedText);
        Assert.False(viewModel.Transfer.HasFailures);
        Assert.False(viewModel.Transfer.CanRetryItems);
        Assert.True(File.Exists(_destination.Combine("IMG_2.JPG")));
        Assert.Equal(2, _session.LastDestinationScan?.TotalFiles); // The dashboard count includes the new files.

        await viewModel.Transfer.OpenFolderCommand.ExecuteAsync(null);
        viewModel.Transfer.DoneCommand.Execute(null);

        Assert.Equal(_destination.Path, Assert.Single(_shell.OpenedFolders));
        Assert.True(viewModel.Transfer.IsIdle);
        Assert.Null(_session.Plan);
        Assert.True(viewModel.ShowIntro);
    }

    [Fact]
    public async Task Failed_files_are_listed_and_can_be_retried()
    {
        var content = new byte[300_000];
        content[0] = 7;
        var flaky = _phone.AddFile("Internal Storage/a/IMG_1.JPG", content);
        _phone.FailReadsOf(flaky, afterBytes: 1_000, ErrorKind.DeviceIo);
        await ConnectPhoneAsync();
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Path, Organization = FolderOrganization.Flat });
        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        await viewModel.Transfer.StartCommand.ExecuteAsync(null);

        Assert.True(viewModel.Transfer.HasFailures);
        var failed = Assert.Single(viewModel.Transfer.FailedFiles);
        Assert.Equal("IMG_1.JPG", failed.FileName);
        Assert.Equal(Strings.ErrorDeviceIo, failed.Reason);
        Assert.Equal(Strings.RetryFailed, viewModel.Transfer.RetryText);
        Assert.True(viewModel.Transfer.RetryCommand.CanExecute(null));

        _phone.HealReadsOf(flaky);
        await viewModel.Transfer.RetryCommand.ExecuteAsync(null);

        Assert.Equal("1", viewModel.Transfer.TransferredText);
        Assert.False(viewModel.Transfer.HasFailures);
        Assert.Equal(content, File.ReadAllBytes(_destination.Combine("IMG_1.JPG")));
    }

    [Fact]
    public async Task Possible_duplicates_are_shown_and_left_out_when_the_user_unticks_them()
    {
        File.WriteAllBytes(_destination.Combine("Holiday.jpg"), TestImages.Render(21, 400, 300));
        var photo = _phone.AddFile("Internal Storage/a/IMG_1.PNG", TestImages.Render(21, 1200, 900, TestImageFormat.Png));
        _phone.SetThumbnail(photo, TestImages.Render(21, 160, 120));
        await ConnectPhoneAsync();
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Path, Organization = FolderOrganization.Flat });
        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.Equal("1", viewModel.PossibleDuplicateText);
        Assert.True(viewModel.HasPossibleDuplicates);
        Assert.False(viewModel.HasNothingNew);
        Assert.True(viewModel.Transfer.StartCommand.CanExecute(null));

        _shell.OptionResult = false;
        await viewModel.Transfer.StartCommand.ExecuteAsync(null);

        var confirmation = Assert.Single(_shell.Confirmations);
        Assert.NotNull(confirmation.OptionLabel);
        Assert.True(confirmation.OptionChecked);
        Assert.Contains(confirmation.Details, detail => detail.Key == Strings.ConfirmPossibleDuplicatesLabel && detail.Value == "1");
        Assert.False(File.Exists(_destination.Combine("IMG_1.PNG")));
        Assert.True(viewModel.Transfer.HasPossibleDuplicates);
        Assert.Equal(Strings.PossibleDuplicatesSkippedLabel, viewModel.Transfer.PossibleDuplicatesLabel);
    }

    [Fact]
    public async Task Start_is_unavailable_when_there_is_nothing_new()
    {
        var shared = new byte[1_000];
        shared[0] = 3;
        File.WriteAllBytes(_destination.Combine("existing.jpg"), shared);
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", shared);
        await ConnectPhoneAsync();
        var viewModel = Create(new AppSettings { DestinationFolder = _destination.Path });

        await viewModel.AnalyzeCommand.ExecuteAsync(null);

        Assert.False(viewModel.Transfer.StartCommand.CanExecute(null));
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
        var lookup = new DestinationContentLookup(_repository, new Sha256HashService(), NullLogger<DestinationContentLookup>.Instance);
        var detector = new ExactDuplicateDetector(_phone, lookup, _history, new Sha256HashService(), NullLogger<ExactDuplicateDetector>.Instance);
        var visual = new VisualDuplicateDetector(_phone, _repository, new WicPerceptualHashService(NullLogger<WicPerceptualHashService>.Instance), NullLogger<VisualDuplicateDetector>.Instance);
        var analysis = new ImportAnalysisService(phoneScan, index, detector, visual, _session, NullLogger<ImportAnalysisService>.Instance);
        var transfers = new MediaTransferService(
            _phone,
            new Sha256HashService(),
            new CaptureDateReader(),
            lookup,
            _repository,
            _history,
            new DestinationNameReservations(),
            TimeProvider.System,
            NullLogger<MediaTransferService>.Instance);
        var fakeSettings = new FakeSettingsService(settings);
        var transfer = new TransferViewModel(transfers, index, _session, fakeSettings, _shell, new InlineUiDispatcher(), NullLogger<TransferViewModel>.Instance);
        return new ImportViewModel(analysis, _session, fakeSettings, _device, transfer, new InlineUiDispatcher());
    }

    public void Dispose() => _destination.Dispose();
}
