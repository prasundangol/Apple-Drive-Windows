using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>Destination folder selection and indexing on the dashboard.</summary>
public sealed partial class DestinationViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly DestinationIndexService _indexService;
    private readonly ImportSession _session;
    private readonly IShellServices _shell;
    private readonly IUiDispatcher _dispatcher;

    public DestinationViewModel(
        ISettingsService settings,
        DestinationIndexService indexService,
        ImportSession session,
        IShellServices shell,
        IUiDispatcher dispatcher)
    {
        _settings = settings;
        _indexService = indexService;
        _session = session;
        _shell = shell;
        _dispatcher = dispatcher;
        Folder = settings.Current.DestinationFolder ?? string.Empty;
        settings.SettingsChanged += OnSettingsChanged;
        ApplySummary(session.LastDestinationScan);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFolder), nameof(HasNoFolder))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial string Folder { get; private set; }

    public bool HasFolder => Folder.Length > 0;

    public bool HasNoFolder => !HasFolder;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ChangesText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorDetails { get; private set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    /// <summary>The last successful scan of the current folder, or <c>null</c>.</summary>
    public DestinationScanSummary? Summary { get; private set; }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var folder = await _shell.PickFolderAsync();
        if (folder is not null)
        {
            await _settings.UpdateAsync(current => current with { DestinationFolder = folder });
        }
    }

    private bool CanScan() => HasFolder && !IsScanning;

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        IsScanning = true;
        ErrorMessage = string.Empty;
        ErrorDetails = string.Empty;
        ProgressText = Strings.Format(Strings.DestinationScanningFormat, 0);
        try
        {
            var progress = new Progress<DestinationScanProgress>(value =>
                ProgressText = Strings.Format(Strings.DestinationScanningFormat, value.FilesFound));
            var result = await _indexService.SyncAsync(Folder, progress, cancellationToken);
            if (result.IsSuccess)
            {
                _session.SetDestinationScan(result.Value);
                ApplySummary(result.Value);
                return;
            }

            ErrorMessage = result.Error.Kind == ErrorKind.Cancelled ? Strings.ScanCancelled : ErrorMessages.For(result.Error);
            ErrorDetails = result.Error.Kind == ErrorKind.Cancelled ? string.Empty : result.Error.ToString();
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings) =>
        _dispatcher.Post(() =>
        {
            var folder = settings.DestinationFolder ?? string.Empty;
            if (!string.Equals(folder, Folder, StringComparison.OrdinalIgnoreCase))
            {
                Folder = folder;
                _session.SetDestinationScan(null);
                ApplySummary(null);
                ErrorMessage = string.Empty;
                ErrorDetails = string.Empty;
            }
        });

    private void ApplySummary(DestinationScanSummary? summary)
    {
        // A summary for a different folder (the setting changed since) does not apply.
        if (summary is not null && (!HasFolder || !IsSameFolder(summary.Root, Folder)))
        {
            summary = null;
        }

        Summary = summary;
        StatusText = summary is null
            ? Strings.DestinationNeverScanned
            : Strings.Format(Strings.DestinationIndexedFormat, summary.TotalFiles);
        ChangesText = HasMeaningfulChanges(summary)
            ? Strings.Format(Strings.DestinationChangesFormat, summary!.NewFiles + summary.ChangedFiles, summary.MissingFiles)
            : string.Empty;
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// Changes are worth reporting only relative to an earlier index; on the first scan of a
    /// folder every file is "new", which says nothing useful.
    /// </summary>
    private static bool HasMeaningfulChanges(DestinationScanSummary? summary)
    {
        if (summary is null)
        {
            return false;
        }

        var hadPreviousIndex = summary.UnchangedFiles + summary.ChangedFiles + summary.MissingFiles > 0;
        var changes = summary.NewFiles + summary.ChangedFiles + summary.MissingFiles;
        return hadPreviousIndex && changes > 0;
    }

    private static bool IsSameFolder(string normalizedRoot, string folder) =>
        string.Equals(normalizedRoot, DestinationIndexService.NormalizeRoot(folder), StringComparison.OrdinalIgnoreCase);
}
