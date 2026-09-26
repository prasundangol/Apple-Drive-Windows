using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Settings;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.ViewModels;
using AppleDrive.Testing;

namespace AppleDrive.UnitTests.Presentation;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly TemporaryDirectory _data = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeShellServices _shell = new();
    private readonly FakeThumbnailService _previews = new();

    [Fact]
    public void Shows_where_previews_are_kept_and_how_much_space_they_use()
    {
        var viewModel = Create();

        Assert.Equal(_previews.CacheFolder, viewModel.PreviewFolder);
        Assert.Equal(Strings.Format(Strings.PreviewsSizeFormat, 3, "45 KB"), viewModel.PreviewSizeText);
        Assert.False(viewModel.UseDefaultPreviewFolderCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_chosen_folder_gets_its_own_previews_subfolder()
    {
        var viewModel = Create();
        _shell.NextPickedFolder = @"E:\Pictures";

        await viewModel.ChoosePreviewFolderCommand.ExecuteAsync(null);

        Assert.Equal(Path.Combine(@"E:\Pictures", SettingsViewModel.PreviewFolderName), _settings.Current.ThumbnailCacheFolder);
        Assert.True(viewModel.UseDefaultPreviewFolderCommand.CanExecute(null));

        await viewModel.UseDefaultPreviewFolderCommand.ExecuteAsync(null);

        Assert.Null(_settings.Current.ThumbnailCacheFolder);
    }

    [Fact]
    public async Task Clearing_previews_updates_the_size()
    {
        var viewModel = Create();

        await viewModel.ClearPreviewsCommand.ExecuteAsync(null);

        Assert.Equal(1, _previews.ClearCount);
        Assert.Equal(Strings.Format(Strings.PreviewsSizeFormat, 0, "0 B"), viewModel.PreviewSizeText);
    }

    private SettingsViewModel Create() =>
        new(_settings, new NoLogLevel(), _shell, new AppPaths(_data.Path), _previews);

    public void Dispose() => _data.Dispose();

    private sealed class NoLogLevel : ILogLevelController
    {
        public void SetLevel(DiagnosticLogLevel level)
        {
        }
    }
}
