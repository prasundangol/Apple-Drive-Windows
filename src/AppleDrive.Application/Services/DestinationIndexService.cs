using System.Diagnostics;
using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Media;
using AppleDrive.Domain.Results;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Application.Services;

/// <summary>Running totals reported while a destination folder is scanned.</summary>
public sealed record DestinationScanProgress(int FilesFound);

/// <summary>Outcome of synchronizing the index with a destination folder.</summary>
public sealed record DestinationScanSummary(
    string Root,
    int TotalFiles,
    int NewFiles,
    int ChangedFiles,
    int UnchangedFiles,
    int MissingFiles,
    TimeSpan Elapsed);

/// <summary>
/// Brings the media index up to date with a destination folder. Unchanged files (same size and
/// modification time) keep their stored hashes, so later scans are cheap.
/// </summary>
/// <remarks>
/// Safety: records are only marked missing after a complete, successful enumeration. A
/// cancelled scan or a drive that disappears mid-scan leaves existing records untouched.
/// </remarks>
public sealed class DestinationIndexService(
    IDestinationScanner scanner,
    IMediaRepository repository,
    TimeProvider clock,
    ILogger<DestinationIndexService> logger)
{
    private const int BatchSize = 500;

    public async Task<Result<DestinationScanSummary>> SyncAsync(
        string root,
        IProgress<DestinationScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        string normalizedRoot;
        try
        {
            normalizedRoot = NormalizeRoot(root);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new AppError(ErrorKind.DestinationUnavailable, $"Invalid destination path: {exception.Message}");
        }

        if (!Directory.Exists(normalizedRoot))
        {
            logger.LogWarning("Destination folder is not available");
            return new AppError(ErrorKind.DestinationUnavailable, "The destination folder does not exist or its drive is not connected.");
        }

        logger.LogInformation("Destination scan started");
        var watch = Stopwatch.StartNew();
        var now = clock.GetUtcNow();

        try
        {
            var existing = (await repository.GetUnderRootAsync(normalizedRoot, cancellationToken).ConfigureAwait(false))
                .ToDictionary(file => file.FullPath, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<long>();
            var pending = new List<IndexedMediaFile>(BatchSize);
            int total = 0, added = 0, changed = 0, unchanged = 0;

            await foreach (var file in scanner.EnumerateAsync(normalizedRoot, cancellationToken).ConfigureAwait(false))
            {
                total++;
                if (existing.TryGetValue(file.FullPath, out var record))
                {
                    seen.Add(record.Id);
                    var sameContentSignature = record.FileSize == file.Size && SameTimestamp(record.ModifiedAt, file.ModifiedAt);
                    if (sameContentSignature && record.IsAvailable)
                    {
                        unchanged++;
                    }
                    else if (sameContentSignature)
                    {
                        // The same file is back (e.g. its drive was reconnected): keep its hashes.
                        unchanged++;
                        pending.Add(record with { IsAvailable = true, LastScannedAt = now });
                    }
                    else
                    {
                        changed++;
                        pending.Add(ToRecord(file, now, firstSeen: record.FirstSeenAt));
                    }
                }
                else
                {
                    added++;
                    pending.Add(ToRecord(file, now, firstSeen: now));
                }

                if (pending.Count >= BatchSize)
                {
                    await repository.UpsertAsync(pending, cancellationToken).ConfigureAwait(false);
                    pending.Clear();
                }

                if (total % 100 == 0)
                {
                    progress?.Report(new DestinationScanProgress(total));
                }
            }

            if (pending.Count > 0)
            {
                await repository.UpsertAsync(pending, cancellationToken).ConfigureAwait(false);
            }

            // Enumeration completed, so anything not seen really is gone from this folder.
            var missing = existing.Values.Where(file => file.IsAvailable && !seen.Contains(file.Id)).Select(file => file.Id).ToList();
            if (missing.Count > 0)
            {
                await repository.MarkUnavailableAsync(missing, cancellationToken).ConfigureAwait(false);
            }

            await repository.TouchScannedAsync(normalizedRoot, now, cancellationToken).ConfigureAwait(false);
            progress?.Report(new DestinationScanProgress(total));

            var summary = new DestinationScanSummary(normalizedRoot, total, added, changed, unchanged, missing.Count, watch.Elapsed);
            logger.LogInformation(
                "Destination scan finished in {Elapsed}: {Total} files ({New} new, {Changed} changed, {Unchanged} unchanged, {Missing} missing)",
                summary.Elapsed, total, added, changed, unchanged, missing.Count);
            return summary;
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Destination scan cancelled");
            return AppError.Cancelled;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Destination scan failed");
            var kind = exception is UnauthorizedAccessException ? ErrorKind.AccessDenied : ErrorKind.DestinationUnavailable;
            return new AppError(kind, exception.Message, exception.HResult);
        }
        catch (DatabaseException exception)
        {
            logger.LogError(exception, "Media index error during destination scan");
            return new AppError(ErrorKind.Database, exception.Message);
        }
    }

    /// <summary>Full path without a trailing separator (except for drive roots such as <c>D:\</c>).</summary>
    public static string NormalizeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var trimmed = Path.TrimEndingDirectorySeparator(full);
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + Path.DirectorySeparatorChar : trimmed;
    }

    private static IndexedMediaFile ToRecord(ScannedFile file, DateTimeOffset now, DateTimeOffset firstSeen) => new()
    {
        FullPath = file.FullPath,
        MediaType = MediaFormats.GetMediaType(file.FullPath),
        FileSize = file.Size,
        CreatedAt = file.CreatedAt,
        ModifiedAt = file.ModifiedAt,
        FirstSeenAt = firstSeen,
        LastScannedAt = now,
        IsAvailable = true,
    };

    /// <summary>The index stores milliseconds, so compare at that precision.</summary>
    private static bool SameTimestamp(DateTimeOffset stored, DateTimeOffset onDisk) =>
        stored.ToUnixTimeMilliseconds() == onDisk.ToUnixTimeMilliseconds();
}
