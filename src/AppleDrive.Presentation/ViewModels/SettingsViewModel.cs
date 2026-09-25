using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Settings;
using AppleDrive.Domain.Enums;
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
    private bool _loading;

    public SettingsViewModel(ISettingsService settings, ILogLevelController logLevel, IShellServices shell, IAppPaths paths)
    {
        _settings = settings;
        _logLevel = logLevel;
        _shell = shell;
        _paths = paths;
        Load(settings.Current);
    }

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
