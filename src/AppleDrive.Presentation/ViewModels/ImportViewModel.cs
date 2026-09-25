using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AppleDrive.Presentation.ViewModels;

/// <summary>Import page: runs the pre-import analysis and shows what a transfer would do.</summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly ImportAnalysisService _analysis;
    private readonly ImportSession _session;
    private readonly ISettingsService _settings;
    private readonly DeviceStatusViewModel _device;

    public ImportViewModel(
        ImportAnalysisService analysis,
        ImportSession session,
        ISettingsService settings,
        DeviceStatusViewModel device,
        IUiDispatcher dispatcher)
    {
        _analysis = analysis;
        _session = session;
        _settings = settings;
        _device = device;

        device.ConnectionChanged += (_, _) => RefreshPrerequisites();
        settings.SettingsChanged += (_, _) => dispatcher.Post(RefreshPrerequisites);
        session.Changed += (_, _) => dispatcher.Post(() => ApplyPlan(session.Plan));
        RefreshPrerequisites();
        ApplyPlan(session.Plan);
    }

    /// <summary>What the user must do before analysing, or empty when ready.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrerequisiteMessage))]
    public partial string PrerequisiteMessage { get; private set; } = string.Empty;

    public bool HasPrerequisiteMessage => PrerequisiteMessage.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial bool IsAnalyzing { get; private set; }

    [ObservableProperty]
    public partial string StageText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StageDetail { get; private set; } = string.Empty;

    /// <summary>Percent complete for the comparison stage (0 while indeterminate).</summary>
    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorDetails { get; private set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan), nameof(HasNothingNew), nameof(ShowIntro))]
    public partial ImportPlan? Plan { get; private set; }

    public bool HasPlan => Plan is not null;

    public bool HasNothingNew => Plan is { TotalCount: > 0, NewCount: 0 };

    public bool ShowIntro => Plan is null && !IsAnalyzing;

    [ObservableProperty]
    public partial string TotalText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string NewText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DuplicateText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string VideoText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TransferSizeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DestinationText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnverified))]
    public partial string UnverifiedText { get; private set; } = string.Empty;

    public bool HasUnverified => UnverifiedText.Length > 0;

    private bool CanAnalyze() => !IsAnalyzing && PrerequisiteMessage.Length == 0;

    [RelayCommand(CanExecute = nameof(CanAnalyze), IncludeCancelCommand = true)]
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        var destination = _settings.Current.DestinationFolder;
        if (destination is null)
        {
            return;
        }

        IsAnalyzing = true;
        OnPropertyChanged(nameof(ShowIntro));
        ErrorMessage = string.Empty;
        ErrorDetails = string.Empty;
        ShowProgress(new AnalysisProgress(AnalysisStage.ScanningPhone, 0));
        try
        {
            var result = await _analysis.AnalyzeAsync(destination, new Progress<AnalysisProgress>(ShowProgress), cancellationToken);
            if (result.IsSuccess)
            {
                ApplyPlan(result.Value);
                return;
            }

            ErrorMessage = result.Error.Kind == ErrorKind.Cancelled ? Strings.ScanCancelled : ErrorMessages.For(result.Error);
            ErrorDetails = result.Error.Kind == ErrorKind.Cancelled ? string.Empty : result.Error.ToString();
        }
        finally
        {
            IsAnalyzing = false;
            OnPropertyChanged(nameof(ShowIntro));
        }
    }

    private void ShowProgress(AnalysisProgress progress)
    {
        StageText = progress.Stage switch
        {
            AnalysisStage.ScanningPhone => Strings.StageScanningPhone,
            AnalysisStage.ScanningDestination => Strings.StageScanningDestination,
            _ => Strings.StageCheckingDuplicates,
        };

        if (progress.Total is { } total and > 0)
        {
            StageDetail = Strings.Format(Strings.StageProgressFormat, progress.Done, total);
            IsProgressIndeterminate = false;
            ProgressPercent = 100.0 * progress.Done / total;
        }
        else
        {
            StageDetail = Strings.Format(Strings.StageCountFormat, progress.Done);
            IsProgressIndeterminate = true;
            ProgressPercent = 0;
        }
    }

    private void RefreshPrerequisites()
    {
        PrerequisiteMessage = !_device.IsConnected
            ? Strings.ImportNeedsDevice
            : string.IsNullOrEmpty(_settings.Current.DestinationFolder) ? Strings.ImportNeedsDestination : string.Empty;
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    private void ApplyPlan(ImportPlan? plan)
    {
        Plan = plan;
        if (plan is null)
        {
            return;
        }

        TotalText = plan.TotalCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        NewText = plan.NewCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        DuplicateText = plan.ExactDuplicateCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        VideoText = plan.VideoCount.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
        TransferSizeText = ByteSize.Format(plan.TransferBytes);
        DestinationText = plan.DestinationRoot;
        UnverifiedText = plan.UnverifiedFiles > 0 ? Strings.Format(Strings.UnverifiedFormat, plan.UnverifiedFiles) : string.Empty;
    }
}
