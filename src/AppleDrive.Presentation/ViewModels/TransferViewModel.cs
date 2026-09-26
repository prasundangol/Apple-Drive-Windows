using System.Globalization;
using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;
using AppleDrive.Presentation.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Presentation.ViewModels;

public enum TransferViewState
{
    Idle,
    Transferring,
    Finished,
}

/// <summary>A file that could not be copied, for the summary's failure list.</summary>
public sealed record FailedFileRow(string FileName, string Reason);

/// <summary>Import page, transfer part: confirmation, live progress, and the summary afterwards.</summary>
public sealed partial class TransferViewModel : ObservableObject
{
    private readonly MediaTransferService _transfers;
    private readonly DestinationIndexService _index;
    private readonly ImportSession _session;
    private readonly ISettingsService _settings;
    private readonly IShellServices _shell;
    private readonly ILogger<TransferViewModel> _logger;
    private CancellationTokenSource? _cancellation;
    private Task? _running;

    public TransferViewModel(
        MediaTransferService transfers,
        DestinationIndexService index,
        ImportSession session,
        ISettingsService settings,
        IShellServices shell,
        IUiDispatcher dispatcher,
        ILogger<TransferViewModel> logger)
    {
        _logger = logger;
        _transfers = transfers;
        _index = index;
        _session = session;
        _settings = settings;
        _shell = shell;
        session.Changed += (_, _) => dispatcher.Post(StartCommand.NotifyCanExecuteChanged);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsTransferring), nameof(IsFinished))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand), nameof(RetryCommand))]
    public partial TransferViewState State { get; private set; }

    public bool IsIdle => State == TransferViewState.Idle;

    public bool IsTransferring => State == TransferViewState.Transferring;

    public bool IsFinished => State == TransferViewState.Finished;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    public partial bool IsCancelling { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorDetails { get; private set; } = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    // Progress

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string ItemsText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentFileText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SpeedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RemainingText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TransferredText { get; private set; } = "0";

    [ObservableProperty]
    public partial string SkippedText { get; private set; } = "0";

    [ObservableProperty]
    public partial string FailedText { get; private set; } = "0";

    [ObservableProperty]
    public partial string BytesText { get; private set; } = string.Empty;

    // Summary

    [ObservableProperty]
    public partial TransferRunResult? LastResult { get; private set; }

    [ObservableProperty]
    public partial string SummaryTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string DestinationText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStopMessage))]
    public partial string StopMessage { get; private set; } = string.Empty;

    public bool HasStopMessage => StopMessage.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailures))]
    public partial string FailuresText { get; private set; } = string.Empty;

    public bool HasFailures => FailuresText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRemaining))]
    public partial string RemainingItemsText { get; private set; } = string.Empty;

    public bool HasRemaining => RemainingItemsText.Length > 0;

    [ObservableProperty]
    public partial IReadOnlyList<FailedFileRow> FailedFiles { get; private set; } = [];

    [ObservableProperty]
    public partial string RetryText { get; private set; } = string.Empty;

    /// <summary>Possible duplicates in the last run: how many were copied, or not copied if the user left them out.</summary>
    [ObservableProperty]
    public partial string PossibleDuplicatesValue { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PossibleDuplicatesLabel { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasPossibleDuplicates { get; private set; }

    /// <summary>The last run left items to copy (failed or not reached).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryCommand))]
    public partial bool CanRetryItems { get; private set; }

    private bool CanStart() => IsIdle && _session.Plan is { HasWork: true };

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var plan = _session.Plan;
        if (plan is null)
        {
            return;
        }

        var settings = _settings.Current;
        List<KeyValuePair<string, string>> details =
        [
            new(Strings.ConfirmNewLabel, Count(plan.NewCount)),
            new(Strings.ConfirmDuplicatesLabel, Count(plan.ExactDuplicateCount)),
        ];
        if (plan.PossibleDuplicateCount > 0)
        {
            details.Add(new(Strings.ConfirmPossibleDuplicatesLabel, Count(plan.PossibleDuplicateCount)));
        }

        details.Add(new(Strings.ConfirmSizeLabel, ByteSize.Format(plan.TransferBytes)));
        details.Add(new(Strings.DestinationLabel, plan.DestinationRoot));
        details.Add(new(Strings.ConfirmOrganizationLabel, OrganizationText(settings.Organization)));

        // Possible duplicates are copied unless the user unticks the box: keeping both never loses a photo.
        var answer = await _shell.ConfirmAsync(new ConfirmationRequest(
            Strings.ConfirmTransferTitle,
            details,
            Strings.ConfirmFootnote,
            Strings.StartTransfer,
            Strings.Cancel,
            plan.PossibleDuplicateCount > 0
                ? Strings.Format(Strings.ConfirmIncludePossibleDuplicatesFormat, plan.PossibleDuplicateCount, ByteSize.Format(plan.PossibleDuplicateBytes))
                : null,
            OptionChecked: true));
        if (!answer.Confirmed || !IsIdle)
        {
            return;
        }

        await RunAsync(new TransferRequest(
            plan.Items,
            plan.DestinationRoot,
            settings.Organization,
            settings.SkipExactDuplicates,
            _session.LastPhoneScan?.Device.FriendlyName,
            IncludePossibleDuplicates: plan.PossibleDuplicateCount == 0 || answer.OptionChecked));
    }

    private bool CanCancel() => IsTransferring && !IsCancelling;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelling = true;
        CurrentFileText = Strings.Cancel + "…";
        _cancellation?.Cancel();
    }

    private bool CanRetry() => IsFinished && CanRetryItems;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private async Task RetryAsync()
    {
        if (LastResult is not { } last)
        {
            return;
        }

        var settings = _settings.Current;
        await RunAsync(new TransferRequest(
            last.GetRetryItems(),
            last.DestinationRoot,
            settings.Organization,
            settings.SkipExactDuplicates,
            _session.LastPhoneScan?.Device.FriendlyName));
    }

    [RelayCommand]
    private Task OpenFolderAsync() =>
        LastResult is { } result ? _shell.OpenFolderAsync(result.DestinationRoot) : Task.CompletedTask;

    /// <summary>Closes the summary. The plan it was based on is out of date, so it is cleared too.</summary>
    [RelayCommand]
    private void Done()
    {
        LastResult = null;
        CanRetryItems = false;
        FailedFiles = [];
        ErrorMessage = string.Empty;
        ErrorDetails = string.Empty;
        State = TransferViewState.Idle;
        _session.SetPlan(null);
    }

    /// <summary>
    /// Cancels a running transfer and waits (up to <paramref name="timeout"/>) for it to clean up
    /// its temporary files. Called when the app closes; safe to call from the UI thread because
    /// the transfer itself never needs the UI thread to finish.
    /// </summary>
    public void StopForShutdown(TimeSpan timeout)
    {
        var running = _running;
        if (running is null || running.IsCompleted)
        {
            return;
        }

        _cancellation?.Cancel();
        try
        {
            running.Wait(timeout);
        }
        catch (AggregateException)
        {
            // The outcome no longer matters; the app is closing.
        }
    }

    private async Task RunAsync(TransferRequest request)
    {
        ErrorMessage = string.Empty;
        ErrorDetails = string.Empty;
        IsCancelling = false;
        ResetProgress();
        State = TransferViewState.Transferring;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        try
        {
            var task = _transfers.TransferAsync(request, new Progress<TransferProgress>(ShowProgress), cancellation.Token);
            _running = task;
            var result = await task;
            if (!result.IsSuccess)
            {
                ErrorMessage = ErrorMessages.For(result.Error);
                ErrorDetails = result.Error.ToString();
                State = TransferViewState.Idle;
                return;
            }

            ShowSummary(result.Value);
            State = TransferViewState.Finished;
            if (result.Value.TransferredCount > 0)
            {
                await RefreshDestinationAsync(result.Value.DestinationRoot);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The transfer service reports expected failures as results; this is a bug. Show it
            // rather than leaving the page stuck on "Transferring".
            _logger.LogError(exception, "Transfer failed unexpectedly");
            ErrorMessage = Strings.ErrorGeneric;
            ErrorDetails = exception.ToString();
            State = TransferViewState.Idle;
        }
        finally
        {
            _cancellation = null;
            IsCancelling = false;
        }
    }

    /// <summary>Re-reads the destination so the dashboard's count includes the new files. Quick: unchanged files keep their index records.</summary>
    private async Task RefreshDestinationAsync(string root)
    {
        var sync = await _index.SyncAsync(root, null, CancellationToken.None);
        if (sync.IsSuccess)
        {
            _session.SetDestinationScan(sync.Value);
        }
        else
        {
            _logger.LogWarning("Could not refresh the destination after a transfer: {Error}", sync.Error);
        }
    }

    private void ResetProgress()
    {
        ProgressPercent = 0;
        ItemsText = string.Empty;
        CurrentFileText = Strings.TransferStarting;
        SpeedText = string.Empty;
        RemainingText = string.Empty;
        TransferredText = "0";
        SkippedText = "0";
        FailedText = "0";
        BytesText = string.Empty;
    }

    private void ShowProgress(TransferProgress progress)
    {
        if (!IsTransferring)
        {
            return; // A late report after the summary is shown.
        }

        ProgressPercent = progress.BytesTotal > 0
            ? Math.Min(100, 100.0 * progress.BytesDone / progress.BytesTotal)
            : progress.ItemsTotal > 0 ? 100.0 * progress.ItemsDone / progress.ItemsTotal : 0;
        ItemsText = Strings.Format(Strings.TransferItemsFormat, progress.ItemsDone, progress.ItemsTotal);
        if (!IsCancelling && progress.CurrentFile is { } file)
        {
            CurrentFileText = Strings.Format(Strings.TransferCurrentFormat, file);
        }

        SpeedText = progress.BytesPerSecond is { } rate ? Strings.Format(Strings.TransferSpeedFormat, ByteSize.Format((long)rate)) : string.Empty;
        RemainingText = progress.Remaining is { } remaining ? Strings.Format(Strings.TransferRemainingFormat, Duration.Format(remaining)) : string.Empty;
        TransferredText = Count(progress.Transferred);
        SkippedText = Count(progress.Skipped);
        FailedText = Count(progress.Failed);
        BytesText = Strings.Format(Strings.TransferItemsFormat, ByteSize.Format(progress.BytesDone), ByteSize.Format(progress.BytesTotal));
    }

    private void ShowSummary(TransferRunResult result)
    {
        LastResult = result;
        SummaryTitle = result.Status switch
        {
            TransferSessionStatus.Cancelled => Strings.TransferCancelledTitle,
            TransferSessionStatus.Stopped => Strings.TransferStoppedTitle,
            _ => Strings.TransferCompleteTitle,
        };
        TransferredText = Count(result.TransferredCount);
        SkippedText = Count(result.SkippedCount);
        FailedText = Count(result.FailedCount);
        BytesText = ByteSize.Format(result.BytesTransferred);
        DestinationText = result.DestinationRoot;
        StopMessage = result.StopReason is { } reason ? ErrorMessages.For(reason) : string.Empty;
        ErrorDetails = result.StopReason?.ToString() ?? string.Empty;
        FailuresText = result.FailedCount > 0 ? Strings.Format(Strings.TransferFailedFormat, result.FailedCount) : string.Empty;
        RemainingItemsText = result.NotAttemptedCount > 0 ? Strings.Format(Strings.TransferRemainingItemsFormat, result.NotAttemptedCount) : string.Empty;
        FailedFiles = result.FailedItems
            .Select(item => new FailedFileRow(item.FileName, item.Error is { } error ? ErrorMessages.For(error) : Strings.ErrorGeneric))
            .ToList();
        RetryText = result.FailedCount > 0 ? Strings.RetryFailed : Strings.CopyRemaining;
        CanRetryItems = result.FailedCount > 0 || result.NotAttemptedCount > 0;
        (PossibleDuplicatesValue, PossibleDuplicatesLabel) = result.PossibleDuplicatesSkippedCount > 0
            ? (Count(result.PossibleDuplicatesSkippedCount), Strings.PossibleDuplicatesSkippedLabel)
            : (Count(result.PossibleDuplicatesCopiedCount), Strings.PossibleDuplicatesCopiedLabel);
        HasPossibleDuplicates = result.PossibleDuplicatesSkippedCount + result.PossibleDuplicatesCopiedCount > 0;
    }

    private static string Count(int value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static string OrganizationText(FolderOrganization organization) => organization switch
    {
        FolderOrganization.YearMonth => Strings.OrganizationYearMonth,
        FolderOrganization.YearMonthDay => Strings.OrganizationYearMonthDay,
        _ => Strings.OrganizationFlat,
    };
}
