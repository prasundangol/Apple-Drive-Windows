using AppleDrive.Domain.Results;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Application.Services;

public enum AnalysisStage
{
    ScanningPhone,
    ScanningDestination,
    CheckingDuplicates,
    PreparingVisualCheck,
    ComparingVisually,
}

/// <summary>Where an analysis is: the stage and a count meaningful for that stage.</summary>
public sealed record AnalysisProgress(AnalysisStage Stage, int Done, int? Total = null);

/// <summary>
/// Runs the pre-import analysis: scan the phone, bring the destination index up to date, classify
/// every phone item byte for byte, then look for visually similar images among the new ones.
/// The resulting plan is stored in the <see cref="ImportSession"/>.
/// </summary>
public sealed class ImportAnalysisService(
    PhoneScanService phoneScan,
    DestinationIndexService destinationIndex,
    ExactDuplicateDetector duplicateDetector,
    VisualDuplicateDetector visualDetector,
    ImportSession session,
    ILogger<ImportAnalysisService> logger)
{
    public async Task<Result<ImportPlan>> AnalyzeAsync(
        string destinationRoot,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Import analysis started");

        progress?.Report(new AnalysisProgress(AnalysisStage.ScanningPhone, 0));
        var phone = await phoneScan.ScanAsync(
            Relay<int>(progress, count => new AnalysisProgress(AnalysisStage.ScanningPhone, count)),
            cancellationToken).ConfigureAwait(false);
        if (!phone.IsSuccess)
        {
            return phone.Error;
        }

        session.SetPhoneScan(phone.Value);

        progress?.Report(new AnalysisProgress(AnalysisStage.ScanningDestination, 0));
        var destination = await destinationIndex.SyncAsync(
            destinationRoot,
            Relay<DestinationScanProgress>(progress, value => new AnalysisProgress(AnalysisStage.ScanningDestination, value.FilesFound)),
            cancellationToken).ConfigureAwait(false);
        if (!destination.IsSuccess)
        {
            return destination.Error;
        }

        session.SetDestinationScan(destination.Value);

        var items = phone.Value.Items;
        progress?.Report(new AnalysisProgress(AnalysisStage.CheckingDuplicates, 0, items.Count));
        var plan = await duplicateDetector.ClassifyAsync(
            items,
            destination.Value.Root,
            Relay<DuplicateCheckProgress>(progress, value => new AnalysisProgress(AnalysisStage.CheckingDuplicates, value.ItemsChecked, value.TotalItems)),
            cancellationToken).ConfigureAwait(false);
        if (!plan.IsSuccess)
        {
            return plan.Error;
        }

        var visual = await visualDetector.CheckAsync(
            plan.Value,
            Relay<VisualCheckProgress>(progress, value => new AnalysisProgress(
                value.Stage == VisualCheckStage.PreparingDestination ? AnalysisStage.PreparingVisualCheck : AnalysisStage.ComparingVisually,
                value.Done,
                value.Total)),
            cancellationToken).ConfigureAwait(false);
        if (!visual.IsSuccess)
        {
            return visual.Error;
        }

        session.SetPlan(visual.Value);
        return visual.Value;
    }

    private static IProgress<T>? Relay<T>(IProgress<AnalysisProgress>? target, Func<T, AnalysisProgress> map) =>
        target is null ? null : new RelayProgress<T>(value => target.Report(map(value)));

    /// <summary>Forwards synchronously, so the caller's own <see cref="Progress{T}"/> does the thread hop once.</summary>
    private sealed class RelayProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
