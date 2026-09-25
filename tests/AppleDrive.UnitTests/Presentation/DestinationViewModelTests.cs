using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.ViewModels;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.UnitTests.Presentation;

public sealed class DestinationViewModelTests : IDisposable
{
    private readonly TemporaryDirectory _folder = new();
    private readonly FakeShellServices _shell = new();
    private readonly ImportSession _session = new();

    [Fact]
    public void Without_a_folder_asks_the_user_to_choose_one()
    {
        var viewModel = Create(new FakeSettingsService());

        Assert.True(viewModel.HasNoFolder);
        Assert.False(viewModel.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Choosing_a_folder_saves_it_and_enables_scanning()
    {
        var settings = new FakeSettingsService();
        var viewModel = Create(settings);
        _shell.NextPickedFolder = _folder.Path;

        await viewModel.ChooseFolderCommand.ExecuteAsync(null);

        Assert.Equal(_folder.Path, settings.Current.DestinationFolder);
        Assert.Equal(_folder.Path, viewModel.Folder);
        Assert.True(viewModel.ScanCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        var settings = new FakeSettingsService(new AppSettings { DestinationFolder = _folder.Path });
        var viewModel = Create(settings);
        _shell.NextPickedFolder = null;

        await viewModel.ChooseFolderCommand.ExecuteAsync(null);

        Assert.Equal(_folder.Path, viewModel.Folder);
    }

    [Fact]
    public async Task Scan_reports_indexed_count()
    {
        File.WriteAllBytes(_folder.Combine("a.jpg"), [1]);
        File.WriteAllBytes(_folder.Combine("b.mov"), [1]);
        var viewModel = Create(new FakeSettingsService(new AppSettings { DestinationFolder = _folder.Path }));

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasError);
        Assert.Equal(2, viewModel.Summary!.TotalFiles);
        Assert.Equal(Strings.Format(Strings.DestinationIndexedFormat, 2), viewModel.StatusText);
        Assert.Same(viewModel.Summary, _session.LastDestinationScan);
    }

    [Fact]
    public async Task Missing_folder_shows_a_friendly_error()
    {
        var viewModel = Create(new FakeSettingsService(new AppSettings { DestinationFolder = _folder.Combine("unplugged") }));

        await viewModel.ScanCommand.ExecuteAsync(null);

        Assert.Equal(Strings.ErrorDestinationUnavailable, viewModel.ErrorMessage);
        Assert.Null(viewModel.Summary);
    }

    [Fact]
    public async Task Changing_the_folder_discards_the_previous_summary()
    {
        File.WriteAllBytes(_folder.Combine("a.jpg"), [1]);
        var settings = new FakeSettingsService(new AppSettings { DestinationFolder = _folder.Path });
        var viewModel = Create(settings);
        await viewModel.ScanCommand.ExecuteAsync(null);

        await settings.UpdateAsync(s => s with { DestinationFolder = _folder.Combine("other") }, TestContext.Current.CancellationToken);

        Assert.Null(viewModel.Summary);
        Assert.Null(_session.LastDestinationScan);
        Assert.Equal(Strings.DestinationNeverScanned, viewModel.StatusText);
    }

    private DestinationViewModel Create(FakeSettingsService settings)
    {
        var service = new DestinationIndexService(
            new DestinationScanner(NullLogger<DestinationScanner>.Instance),
            new InMemoryMediaRepository(),
            TimeProvider.System,
            NullLogger<DestinationIndexService>.Instance);
        return new DestinationViewModel(settings, service, _session, _shell, new InlineUiDispatcher());
    }

    public void Dispose() => _folder.Dispose();
}
