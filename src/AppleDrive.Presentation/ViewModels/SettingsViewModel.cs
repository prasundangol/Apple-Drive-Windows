using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Settings;
using AppleDrive.Domain.Enums;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>A labelled choice for a settings combo box.</summary>
public sealed record Choice<T>(T Value, string Label);

/// <summary>Settings page.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly ILogLevelController _logLevel;
    private readonly IShellServices _shell;
    private readonly IAppPaths _paths;
    private readonly IThumbnailService _previews;
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, ILogLevelController logLevel, IShellServices shell, IAppPaths paths, IThumbnailService previews)
    {
        _settings = settings;
        _logLevel = logLevel;
        _shell = shell;
        _paths = paths;
        _previews = previews;
        Load(settings.Current);
        _ = RefreshPreviewsAsync();
    }

    /// <summary>Sub-folder created inside a folder the user picks, so the cache never mixes with their files.</summary>
    public const string PreviewFolderName = "Apple Drive previews";

    [ObservableProperty]
    public partial string PreviewFolder { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewSizeText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseDefaultPreviewFolderCommand))]
    public partial bool HasCustomPreviewFolder { get; private set; }

    public IReadOnlyList<Choice<FolderOrganization>> OrganizationChoices { get; } =
    [
        new(FolderOrganization.Flat, Strings.OrganizationFlat),
        new(FolderOrganization.YearMonth, Strings.OrganizationYearMonth),
        new(FolderOrganization.YearMonthDay, Strings.OrganizationYearMonthDay),
    ];

    public IReadOnlyList<Choice<DiagnosticLogLevel>> LogLevelChoices { get; } =
    [
        new(DiagnosticLogLevel.Debug, Strings.LogLevelDebug),
        new(DiagnosticLogLevel.Information, Strings.LogLevelInformation),
        new(DiagnosticLogLevel.Warning, Strings.LogLevelWarning),
        new(DiagnosticLogLevel.Error, Strings.LogLevelError),
    ];

    public string Version { get; } = Strings.Format(
        Strings.VersionFormat,
        typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.0.0");

    [ObservableProperty]
    public partial string DestinationFolder { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial Choice<FolderOrganization>? SelectedOrganization { get; set; }

    [ObservableProperty]
    public partial Choice<DiagnosticLogLevel>? SelectedLogLevel { get; set; }

    [ObservableProperty]
    public partial bool SkipExactDuplicates { get; set; }

    [RelayCommand]
    private async Task BrowseDestinationAsync()
    {
        var folder = await _shell.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        DestinationFolder = folder;
        await _settings.UpdateAsync(current => current with { DestinationFolder = folder });
    }

    [RelayCommand]
    private async Task ChoosePreviewFolderAsync()
    {
        var folder = await _shell.PickFolderAsync();
        if (folder is null)
        {
            return;
        }

        await _settings.UpdateAsync(current => current with { ThumbnailCacheFolder = Path.Combine(folder, PreviewFolderName) });
        await RefreshPreviewsAsync();
    }

    private bool CanUseDefaultPreviewFolder() => HasCustomPreviewFolder;

    [RelayCommand(CanExecute = nameof(CanUseDefaultPreviewFolder))]
    private async Task UseDefaultPreviewFolderAsync()
    {
        await _settings.UpdateAsync(current => current with { ThumbnailCacheFolder = null });
        await RefreshPreviewsAsync();
    }

    /// <summary>Deletes cached previews only; they are made again when the review screen needs them.</summary>
    [RelayCommand]
    private async Task ClearPreviewsAsync()
    {
        await _previews.ClearCacheAsync(CancellationToken.None);
        await RefreshPreviewsAsync();
    }

    private async Task RefreshPreviewsAsync()
    {
        PreviewFolder = _previews.CacheFolder;
        HasCustomPreviewFolder = _settings.Current.ThumbnailCacheFolder is not null;
        var (count, bytes) = await _previews.GetCacheSizeAsync(CancellationToken.None);
        PreviewSizeText = Strings.Format(Strings.PreviewsSizeFormat, count, ByteSize.Format(bytes));
    }

    [RelayCommand]
    private async Task OpenLogsFolderAsync()
    {
        Directory.CreateDirectory(_paths.LogsFolder);
        await _shell.OpenFolderAsync(_paths.LogsFolder);
    }

    partial void OnSelectedOrganizationChanged(Choice<FolderOrganization>? value)
    {
        if (!_loading && value is not null)
        {
            _ = _settings.UpdateAsync(current => current with { Organization = value.Value });
        }
    }

    partial void OnSelectedLogLevelChanged(Choice<DiagnosticLogLevel>? value)
    {
        if (!_loading && value is not null)
        {
            _logLevel.SetLevel(value.Value);
            _ = _settings.UpdateAsync(current => current with { LogLevel = value.Value });
        }
    }

    partial void OnSkipExactDuplicatesChanged(bool value)
    {
        if (!_loading)
        {
            _ = _settings.UpdateAsync(current => current with { SkipExactDuplicates = value });
        }
    }

    private void Load(AppSettings settings)
    {
        _loading = true;
        try
        {
            DestinationFolder = settings.DestinationFolder ?? Strings.DestinationNotSet;
            SelectedOrganization = OrganizationChoices.FirstOrDefault(choice => choice.Value == settings.Organization);
            SelectedLogLevel = LogLevelChoices.FirstOrDefault(choice => choice.Value == settings.LogLevel);
            SkipExactDuplicates = settings.SkipExactDuplicates;
        }
        finally
        {
            _loading = false;
        }
    }
}
