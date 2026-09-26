using System.Text.RegularExpressions;
using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Application.Services;

/// <summary>What startup recovery found and did.</summary>
/// <param name="Sessions">Interrupted sessions that were closed.</param>
/// <param name="Completed">Files that had been renamed into place and verified, now recorded as transferred.</param>
/// <param name="TemporaryFilesRemoved">Incomplete <c>.partial</c> files deleted.</param>
/// <param name="Interrupted">Transfers closed as interrupted (nothing was kept).</param>
/// <param name="SessionsDeferred">Sessions left for later because their destination is not connected.</param>
public sealed record RecoveryReport(int Sessions, int Completed, int TemporaryFilesRemoved, int Interrupted, int SessionsDeferred);

/// <summary>
/// Cleans up after the app stopped mid-transfer (crash, power loss, killed process). Run once at
/// startup, before any transfer can start.
/// </summary>
/// <remarks>
/// For each unfinished transfer: if its final file exists and matches the recorded SHA-256, the
/// rename had happened, so it is recorded as transferred and indexed. Otherwise its recorded
/// <c>.partial</c> file (and only that) is deleted. Nothing else is ever deleted. Sessions whose
/// destination drive is not connected are left untouched and retried at the next start.
/// </remarks>
public sealed partial class TransferRecoveryService(
    ITransferRepository transfers,
    IMediaRepository media,
    IHashService hashService,
    ICaptureDateReader captureDates,
    TimeProvider clock,
    ILogger<TransferRecoveryService> logger)
{
    public async Task<RecoveryReport> RecoverAsync(CancellationToken cancellationToken)
    {
        int sessions = 0, completed = 0, removed = 0, interrupted = 0, deferred = 0;

        // Sessions left running, plus any whose run ended but left a transfer open (for example
        // when the index could not be updated after a file was renamed into place).
        var candidates = (await transfers.GetRunningSessionsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        foreach (var sessionId in (await transfers.GetInProgressAsync(cancellationToken).ConfigureAwait(false)).Select(record => record.SessionId).Distinct())
        {
            if (candidates.All(session => session.Id != sessionId)
                && await transfers.GetSessionAsync(sessionId, cancellationToken).ConfigureAwait(false) is { } ended)
            {
                candidates.Add(ended);
            }
        }

        foreach (var session in candidates)
        {
            if (!Directory.Exists(session.DestinationRoot))
            {
                deferred++;
                logger.LogInformation("An interrupted transfer's destination is not connected; recovery deferred");
                continue;
            }

            var records = await transfers.GetBySessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
            foreach (var record in records.Where(record => record.Status == TransferStatus.InProgress))
            {
                if (await TryCompleteAsync(record, cancellationToken).ConfigureAwait(false))
                {
                    completed++;
                    continue;
                }

                if (RemovePartial(record))
                {
                    removed++;
                }

                await transfers.CompleteAsync(
                    record.Id,
                    new TransferOutcomeRecord(TransferStatus.Interrupted, clock.GetUtcNow(), ErrorKind: "Interrupted", ErrorMessage: "The app stopped before this file was finished."),
                    cancellationToken).ConfigureAwait(false);
                interrupted++;
            }

            var final = await transfers.GetBySessionAsync(session.Id, cancellationToken).ConfigureAwait(false);
            await transfers.CompleteSessionAsync(
                session with
                {
                    Status = session.Status == TransferSessionStatus.Running ? TransferSessionStatus.Interrupted : session.Status,
                    CompletedAt = session.CompletedAt ?? clock.GetUtcNow(),
                    TransferredCount = final.Count(record => record.Status == TransferStatus.Completed),
                    SkippedCount = final.Count(record => record.Status == TransferStatus.Duplicate),
                    FailedCount = final.Count(record => record.Status == TransferStatus.Failed),
                    TransferredBytes = final.Where(record => record.Status == TransferStatus.Completed).Sum(record => record.FileSize ?? 0),
                },
                cancellationToken).ConfigureAwait(false);
            sessions++;
        }

        var report = new RecoveryReport(sessions, completed, removed, interrupted, deferred);
        if (sessions + deferred > 0)
        {
            logger.LogWarning(
                "Recovered {Sessions} interrupted transfer session(s): {Completed} file(s) completed, {Removed} temporary file(s) removed, {Interrupted} transfer(s) closed, {Deferred} deferred",
                sessions, completed, removed, interrupted, deferred);
        }

        return report;
    }

    /// <summary>The file was renamed into place before the app stopped: verify it and record it.</summary>
    private async Task<bool> TryCompleteAsync(TransferRecord record, CancellationToken cancellationToken)
    {
        if (record is not { DestinationPath: { } path, Sha256: { } expected, FileSize: { } size })
        {
            return false;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size)
            {
                return false;
            }

            var actual = await hashService.ComputeFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (!actual.Matches(expected))
            {
                // Not the file this transfer wrote; it is left alone.
                return false;
            }

            var captureDate = await captureDates.ReadAsync(path, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            info.Refresh();
            await media.UpsertAsync(
                [
                    new IndexedMediaFile
                    {
                        FullPath = path,
                        MediaType = MediaFormats.GetMediaType(path),
                        FileSize = info.Length,
                        CreatedAt = info.CreationTimeUtc,
                        ModifiedAt = info.LastWriteTimeUtc,
                        CaptureDate = captureDate?.Value,
                        Sha256 = expected,
                        FirstSeenAt = now,
                        LastScannedAt = now,
                        IsAvailable = true,
                    },
                ],
                cancellationToken).ConfigureAwait(false);
            await transfers.CompleteAsync(record.Id, new TransferOutcomeRecord(TransferStatus.Completed, now, path, expected, size), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Recovered a file that was fully transferred before the app stopped");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not check a file from an interrupted transfer");
            return false;
        }
    }

    /// <summary>Deletes the transfer's own temporary file, recognised by its recorded path and name pattern.</summary>
    private bool RemovePartial(TransferRecord record)
    {
        if (record.PartialPath is not { } partial || !OwnPartialName().IsMatch(Path.GetFileName(partial)) || !File.Exists(partial))
        {
            return false;
        }

        try
        {
            File.Delete(partial);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove a temporary file from an interrupted transfer");
            return false;
        }
    }

    /// <summary><c>&lt;name&gt;.&lt;8 hex digits&gt;.partial</c>, the only shape the transfer engine creates.</summary>
    [GeneratedRegex(@"^.+\.[0-9a-f]{8}\.partial$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OwnPartialName();
}
