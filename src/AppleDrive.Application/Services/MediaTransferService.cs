using System.Buffers;
using System.Security.Cryptography;
using System.Threading.Channels;
using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Domain.Results;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Application.Services;

/// <summary>
/// Copies media from the phone into the destination, safely:
/// <c>phone → name.partial (hashed while copying) → flush → verify size and SHA-256 → rename → index</c>.
/// </summary>
/// <remarks>
/// <para>
/// Reads from the phone are strictly one at a time (the device misbehaves otherwise). A copied
/// file is verified, dated and committed by a second stage while the next file is being read,
/// with at most <see cref="PendingItemLimit"/> copied items waiting.
/// </para>
/// <para>
/// Never overwrites: a name clash gets a <c> (n)</c> suffix. Never deletes anything except its own
/// <c>.partial</c> files. A file is reported as transferred only after verification and rename.
/// </para>
/// </remarks>
public sealed class MediaTransferService(
    IPhonePhotoSource source,
    IHashService hashService,
    ICaptureDateReader captureDates,
    DestinationContentLookup destinationContent,
    IMediaRepository mediaRepository,
    ITransferRepository transferRepository,
    DestinationNameReservations reservations,
    TimeProvider clock,
    ILogger<MediaTransferService> logger)
{
    /// <summary>Suffix of files still being written. Destination scans ignore them.</summary>
    public const string PartialFileSuffix = ".partial";

    /// <summary>Copied items that may wait for verification while the next one is read.</summary>
    private const int PendingItemLimit = 2;

    /// <summary>A file whose read fails or looks wrong is read once more, after reconnecting.</summary>
    private const int MaxReadAttempts = 2;

    private const int BufferSize = 1024 * 1024;

    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public async Task<Result<TransferRunResult>> TransferAsync(
        TransferRequest request,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = source.ConnectedDevice;
        if (device is null)
        {
            return new AppError(ErrorKind.DeviceNotFound, "No device is connected.");
        }

        string root;
        try
        {
            root = DestinationIndexService.NormalizeRoot(request.DestinationRoot);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new AppError(ErrorKind.DestinationUnavailable, $"Invalid destination path: {exception.Message}");
        }

        if (!Directory.Exists(root))
        {
            return new AppError(ErrorKind.DestinationUnavailable, "The destination folder does not exist or its drive is not connected.");
        }

        var run = new Run(request, root, device, clock, progress);
        try
        {
            await transferRepository.CreateSessionAsync(
                new TransferSessionRecord
                {
                    Id = run.SessionId,
                    DeviceName = request.DeviceName ?? device.FriendlyName,
                    DestinationRoot = root,
                    StartedAt = clock.GetUtcNow(),
                    Status = TransferSessionStatus.Running,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return AppError.Cancelled;
        }
        catch (DatabaseException exception)
        {
            logger.LogError(exception, "Could not record the transfer session");
            return new AppError(ErrorKind.Database, exception.Message);
        }

        logger.LogInformation(
            "Transfer {Session} started: {Items} items, {Files} files to copy, about {Bytes} bytes",
            run.SessionId, run.Work.Count, run.Work.Sum(work => work.ToCopy.Count), run.BytesTotal);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        run.Stopper = stop;
        run.Report(force: true, currentFile: null);
        var channel = Channel.CreateBounded<CopiedItem>(new BoundedChannelOptions(PendingItemLimit)
        {
            SingleReader = true,
            SingleWriter = true,
        });

        var finalizer = Task.Run(() => FinalizeAllAsync(channel.Reader, run, cancellationToken), CancellationToken.None);
        try
        {
            await ReadAllAsync(channel.Writer, run, stop.Token).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.TryComplete();
            await finalizer.ConfigureAwait(false);
        }

        var status = cancellationToken.IsCancellationRequested
            ? TransferSessionStatus.Cancelled
            : run.StopReason is not null ? TransferSessionStatus.Stopped : TransferSessionStatus.Completed;
        var result = run.ToResult(status);
        await RecordSessionEndAsync(result).ConfigureAwait(false);
        run.Report(force: true, currentFile: null);

        logger.LogInformation(
            "Transfer {Session} {Status} in {Elapsed}: {Transferred} transferred, {Skipped} skipped, {Failed} failed, {NotAttempted} not attempted, {Bytes} bytes",
            result.SessionId, status, result.Elapsed, result.TransferredCount, result.SkippedCount, result.FailedCount, result.NotAttemptedCount, result.BytesTransferred);
        if (result.StopReason is { } reason)
        {
            logger.LogWarning("Transfer stopped early: {Error}", reason);
        }

        return result;
    }

    // ---------------------------------------------------------------- stage 1: read from the phone

    private async Task ReadAllAsync(ChannelWriter<CopiedItem> writer, Run run, CancellationToken stopToken)
    {
        foreach (var work in run.Work)
        {
            if (stopToken.IsCancellationRequested)
            {
                return;
            }

            var copies = new List<ComponentCopy>(work.ToCopy.Count);
            foreach (var component in work.ToCopy)
            {
                if (stopToken.IsCancellationRequested)
                {
                    break;
                }

                var copy = await CopyWithRetryAsync(component, run, stopToken).ConfigureAwait(false);
                copies.Add(copy);
                if (copy.Error is { } error && StopsTheRun(error))
                {
                    run.Stop(error);
                    break;
                }
            }

            // Not cancellable: the finalizer drains the channel until it is completed, and it
            // decides whether copies are kept (e.g. after the phone is unplugged) or discarded.
            await writer.WriteAsync(new CopiedItem(work, copies), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<ComponentCopy> CopyWithRetryAsync(ComponentClassification component, Run run, CancellationToken stopToken)
    {
        var asset = component.Asset;
        var partialPath = Path.Combine(
            run.Root,
            $"{DestinationLayout.SanitizeFileName(asset.FileName)}.{Guid.NewGuid().ToString("N")[..8]}{PartialFileSuffix}");

        long transferId;
        try
        {
            transferId = await transferRepository.AddAsync(
                new TransferRecord
                {
                    SessionId = run.SessionId,
                    SourceAssetId = asset.Id,
                    SourcePersistentId = asset.PersistentId,
                    SourceFileName = asset.FileName,
                    ReportedSize = asset.ReportedSize,
                    PartialPath = partialPath,
                    StartedAt = clock.GetUtcNow(),
                    Status = TransferStatus.InProgress,
                },
                stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ComponentCopy.Failed(component, 0, AppError.Cancelled);
        }
        catch (DatabaseException exception)
        {
            return ComponentCopy.Failed(component, 0, new AppError(ErrorKind.Database, exception.Message));
        }

        run.SetCurrentFile(asset.FileName);
        byte[]? previousHash = null;
        AppError? lastError = null;
        for (var attempt = 1; attempt <= MaxReadAttempts; attempt++)
        {
            if (attempt > 1)
            {
                var reconnect = await ReconnectAsync(run, stopToken).ConfigureAwait(false);
                if (reconnect is not null)
                {
                    lastError = reconnect;
                    break;
                }
            }

            var bytesBefore = run.BytesRead;
            var copied = await CopyOnceAsync(asset, partialPath, run, stopToken).ConfigureAwait(false);
            if (!copied.IsSuccess)
            {
                DeletePartial(partialPath);
                run.RewindBytes(bytesBefore);
                lastError = copied.Error;
                if (copied.Error.Kind == ErrorKind.Cancelled || StopsTheRun(copied.Error) || !IsWorthRetrying(copied.Error))
                {
                    break;
                }

                logger.LogWarning("Reading {File} failed (attempt {Attempt}): {Error}", asset.FileName, attempt, copied.Error);
                continue;
            }

            var hash = copied.Value;
            var sizeMatches = asset.ReportedSize is null || asset.ReportedSize == hash.Length;
            var hashMatches = component.Sha256 is null || hash.Matches(component.Sha256);
            if (sizeMatches && hashMatches)
            {
                return ComponentCopy.Copied(component, transferId, partialPath, hash, convertedToJpeg: false);
            }

            // Two identical reads, the second after a fresh connection, rule out a stale or
            // mixed-up device stream. Then a changed file (same size) or an on-the-fly HEIC→JPEG
            // conversion (different size, JPEG content) is genuine.
            if (previousHash is not null && hash.Matches(previousHash))
            {
                if (sizeMatches)
                {
                    logger.LogWarning("{File} changed on the phone since it was checked; importing the current version", asset.FileName);
                    return ComponentCopy.Copied(component, transferId, partialPath, hash, convertedToJpeg: false);
                }

                if (IsHeif(asset) && await StartsWithJpegMarkerAsync(partialPath).ConfigureAwait(false))
                {
                    logger.LogInformation("{File} was converted to JPEG by the phone during transfer", asset.FileName);
                    return ComponentCopy.Copied(component, transferId, partialPath, hash, convertedToJpeg: true);
                }
            }

            previousHash = hash.Sha256;
            DeletePartial(partialPath);
            run.RewindBytes(bytesBefore);
            lastError = sizeMatches
                ? new AppError(ErrorKind.VerificationFailed, "The phone delivered different content than when the file was checked.")
                : new AppError(ErrorKind.VerificationFailed, $"The phone reported {asset.ReportedSize} bytes but delivered {hash.Length}.");
            logger.LogWarning("{File}: {Error} (attempt {Attempt})", asset.FileName, lastError, attempt);
        }

        var failure = lastError ?? new AppError(ErrorKind.Unknown, "The file could not be read.");
        await RecordOutcomeAsync(
            transferId,
            failure.Kind == ErrorKind.Cancelled
                ? new TransferOutcomeRecord(TransferStatus.Cancelled, clock.GetUtcNow())
                : new TransferOutcomeRecord(TransferStatus.Failed, clock.GetUtcNow(), ErrorKind: failure.Kind.ToString(), ErrorMessage: failure.Details)).ConfigureAwait(false);
        return ComponentCopy.Failed(component, transferId, failure);
    }

    /// <summary>
    /// Streams one file from the phone into <paramref name="partialPath"/>, hashing as it goes, with
    /// the disk write of each chunk overlapping the device read of the next. Then flushes to disk.
    /// </summary>
    private async Task<Result<HashResult>> CopyOnceAsync(PhotoAsset asset, string partialPath, Run run, CancellationToken cancellationToken)
    {
        var open = await source.OpenAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        if (!open.IsSuccess)
        {
            return open.Error;
        }

        var buffers = new[] { ArrayPool<byte>.Shared.Rent(BufferSize), ArrayPool<byte>.Shared.Rent(BufferSize) };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0;
        try
        {
            await using var input = open.Value;
            FileStream output;
            try
            {
                output = new FileStream(partialPath, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    BufferSize = 0,
                    PreallocationSize = asset.ReportedSize ?? 0,
                });
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return DestinationErrors.FromException(exception, Directory.Exists(run.Root));
            }

            await using (output.ConfigureAwait(false))
            {
                Task pendingWrite = Task.CompletedTask;
                try
                {
                    var current = 0;
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await input.ReadAsync(buffers[current].AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false);
                        }
                        catch (SourceReadException exception)
                        {
                            return exception.Error;
                        }
                        catch (IOException exception)
                        {
                            return new AppError(ErrorKind.DeviceIo, exception.Message, exception.HResult);
                        }

                        var write = await AwaitWriteAsync(pendingWrite, run).ConfigureAwait(false);
                        if (write is not null)
                        {
                            return write;
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        hash.AppendData(buffers[current], 0, read);
                        total += read;
                        run.AddBytes(read);
                        pendingWrite = output.WriteAsync(buffers[current].AsMemory(0, read), cancellationToken).AsTask();
                        current = 1 - current;
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    return DestinationErrors.FromException(exception, Directory.Exists(run.Root));
                }
                finally
                {
                    // A write still in flight must finish before its buffer goes back to the pool.
                    await ObserveAsync(pendingWrite).ConfigureAwait(false);
                }
            }

            return new HashResult(hash.GetHashAndReset(), total);
        }
        catch (OperationCanceledException)
        {
            return AppError.Cancelled;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffers[0]);
            ArrayPool<byte>.Shared.Return(buffers[1]);
        }
    }

    private static async Task<AppError?> AwaitWriteAsync(Task write, Run run)
    {
        try
        {
            await write.ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DestinationErrors.FromException(exception, Directory.Exists(run.Root));
        }
    }

    /// <summary>Waits for an in-flight write; by then its error, if any, has been reported or no longer matters.</summary>
    private static async Task ObserveAsync(Task write)
    {
        try
        {
            await write.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The read failure is what gets reported.
        }
    }

    private async Task<AppError?> ReconnectAsync(Run run, CancellationToken cancellationToken)
    {
        logger.LogInformation("Reconnecting to the phone before reading the file again");
        try
        {
            var result = await source.ConnectAsync(run.Device, cancellationToken).ConfigureAwait(false);
            return result.Status == DeviceConnectionStatus.Connected
                ? null
                : result.Error ?? new AppError(ErrorKind.DeviceDisconnected, "The phone could not be reopened.");
        }
        catch (OperationCanceledException)
        {
            return AppError.Cancelled;
        }
    }

    // ---------------------------------------------------------------- stage 2: verify and commit

    private async Task FinalizeAllAsync(ChannelReader<CopiedItem> reader, Run run, CancellationToken cancellationToken)
    {
        await foreach (var item in reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (cancellationToken.IsCancellationRequested || run.StopReason is { } reason && !IsDeviceError(reason))
            {
                // Cancelled, or the destination/index is unusable: nothing more is committed.
                // After a phone failure, files already copied are still verified and kept.
                await DiscardAsync(item, run).ConfigureAwait(false);
                continue;
            }

            try
            {
                var outcome = await FinalizeItemAsync(item, run, cancellationToken).ConfigureAwait(false);
                run.Complete(item.Work.Index, outcome);
            }
            catch (Exception exception)
            {
                // Never leave the pipeline hanging: whatever went wrong, this item is failed.
                logger.LogError(exception, "Unexpected error while finishing {File}", item.Work.Item.Item.Primary.FileName);
                var error = exception is DatabaseException
                    ? new AppError(ErrorKind.Database, exception.Message)
                    : new AppError(ErrorKind.Unknown, exception.Message);
                run.Stop(error);
                await DiscardAsync(item, run, error).ConfigureAwait(false);
            }
        }
    }

    private async Task<ItemTransferOutcome> FinalizeItemAsync(CopiedItem item, Run run, CancellationToken cancellationToken)
    {
        var outcomes = new Dictionary<ComponentClassification, ComponentTransferOutcome>();
        var toCommit = new List<VerifiedCopy>();

        foreach (var copy in item.Copies)
        {
            if (copy.Error is { } copyError)
            {
                outcomes[copy.Component] = new ComponentTransferOutcome(
                    copy.Component,
                    copyError.Kind == ErrorKind.Cancelled ? ItemTransferStatus.NotAttempted : ItemTransferStatus.Failed,
                    Error: copyError.Kind == ErrorKind.Cancelled ? null : copyError);
                continue;
            }

            var verified = await VerifyAsync(copy, run, cancellationToken).ConfigureAwait(false);
            if (verified.Outcome is { } done)
            {
                outcomes[copy.Component] = done;
            }
            else
            {
                toCommit.Add(verified.Copy!);
            }
        }

        if (toCommit.Count > 0)
        {
            var placement = ChoosePlacement(item, toCommit, outcomes, run);
            foreach (var (copy, outcome) in await CommitAsync(toCommit, placement, run).ConfigureAwait(false))
            {
                outcomes[copy.Copy.Component] = outcome;
            }
        }

        var components = item.Work.Item.Components
            .Select(component => outcomes.TryGetValue(component, out var outcome)
                ? outcome
                : item.Work.ToCopy.Contains(component)
                    ? new ComponentTransferOutcome(component, ItemTransferStatus.NotAttempted)
                    : new ComponentTransferOutcome(component, ItemTransferStatus.AlreadyExists, component.ExistingPath))
            .ToList();
        return new ItemTransferOutcome(item.Work.Item, components);
    }

    /// <summary>
    /// Checks the closed <c>.partial</c> file against what was read from the phone, then makes sure
    /// the content isn't already in the destination (the phone may have reported a wrong size
    /// during the duplicate check). Returns either a copy to commit or a final outcome.
    /// </summary>
    private async Task<(VerifiedCopy? Copy, ComponentTransferOutcome? Outcome)> VerifyAsync(ComponentCopy copy, Run run, CancellationToken cancellationToken)
    {
        var component = copy.Component;
        var partial = copy.PartialPath!;
        var expected = copy.Hash!;
        try
        {
            var info = new FileInfo(partial);
            AppError? mismatch = null;
            if (!info.Exists)
            {
                mismatch = new AppError(ErrorKind.VerificationFailed, "The copied file disappeared before it could be verified.");
            }
            else if (info.Length != expected.Length)
            {
                mismatch = new AppError(ErrorKind.VerificationFailed, $"The copied file has {info.Length} bytes; {expected.Length} were written.");
            }
            else
            {
                var onDisk = await hashService.ComputeFileAsync(partial, cancellationToken).ConfigureAwait(false);
                if (!onDisk.Matches(expected.Sha256))
                {
                    mismatch = new AppError(ErrorKind.VerificationFailed, "The copied file's SHA-256 does not match the data read from the phone.");
                }
            }

            if (mismatch is not null)
            {
                logger.LogWarning("{File} failed verification: {Error}", component.Asset.FileName, mismatch);
                return (null, await FailAsync(copy, mismatch).ConfigureAwait(false));
            }

            if (run.Request.SkipExactDuplicates)
            {
                var existing = await destinationContent.FindIdenticalAsync(expected, run.Root, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    logger.LogInformation("{File} is identical to a file already in the destination; copy discarded", component.Asset.FileName);
                    DeletePartial(partial);
                    await RecordOutcomeAsync(copy.TransferId, new TransferOutcomeRecord(
                        TransferStatus.Duplicate, clock.GetUtcNow(), existing.FullPath, expected.Sha256, expected.Length)).ConfigureAwait(false);
                    return (null, new ComponentTransferOutcome(component, ItemTransferStatus.DuplicateFound, existing.FullPath));
                }
            }

            var captureDate = await ReadCaptureDateAsync(partial, component.Asset.FileName, cancellationToken).ConfigureAwait(false);
            return (new VerifiedCopy(copy, captureDate), null);
        }
        catch (OperationCanceledException)
        {
            DeletePartial(partial);
            await RecordOutcomeAsync(copy.TransferId, new TransferOutcomeRecord(TransferStatus.Cancelled, clock.GetUtcNow())).ConfigureAwait(false);
            return (null, new ComponentTransferOutcome(component, ItemTransferStatus.NotAttempted));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var error = DestinationErrors.FromException(exception, Directory.Exists(run.Root));
            if (DestinationErrors.AffectsAllFiles(error))
            {
                run.Stop(error);
            }

            return (null, await FailAsync(copy, error).ConfigureAwait(false));
        }
    }

    private async Task<CaptureDate?> ReadCaptureDateAsync(string path, string fileName, CancellationToken cancellationToken)
    {
        try
        {
            return await captureDates.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not read the capture date of {File}", fileName);
            return null;
        }
    }

    /// <summary>
    /// Folder and base name for the files of one item. A Live Photo's parts share both; the date
    /// comes from the image. If the image is already in the destination, a missing video goes next
    /// to it with the same name.
    /// </summary>
    private Placement ChoosePlacement(
        CopiedItem item,
        IReadOnlyList<VerifiedCopy> toCommit,
        IReadOnlyDictionary<ComponentClassification, ComponentTransferOutcome> outcomes,
        Run run)
    {
        var primary = item.Work.Item.Components[0];
        if (item.Work.Item.Item.IsLivePhoto && toCommit.All(copy => copy.Copy.Component != primary))
        {
            // The image is already in the destination (known before, or found after copying).
            var existingImage = outcomes.TryGetValue(primary, out var outcome) && outcome.Status == ItemTransferStatus.DuplicateFound
                ? outcome.DestinationPath
                : primary.ExistingPath;
            if (existingImage is not null)
            {
                return new Placement(Path.GetDirectoryName(existingImage) ?? run.Root, Path.GetFileNameWithoutExtension(existingImage));
            }
        }

        // Best date first: the file's own metadata, then the phone's date for it, then the month
        // of its camera-roll folder, and only as a last resort the transfer date.
        var captureDate = toCommit.FirstOrDefault(copy => copy.Copy.Component == primary)?.CaptureDate
            ?? toCommit.Select(copy => copy.CaptureDate).FirstOrDefault(date => date is not null);
        var dayKnown = true;
        DateTime wallClock;
        if (captureDate is not null)
        {
            wallClock = captureDate.Value.DateTime;
        }
        else if ((primary.Asset.CreatedAt ?? primary.Asset.ModifiedAt) is { } phoneDate)
        {
            wallClock = phoneDate.ToLocalTime().DateTime;
        }
        else if (DestinationLayout.MonthFromPhoneFolder(primary.Asset.SourceFolder) is { } folderMonth)
        {
            wallClock = folderMonth;
            dayKnown = false;
        }
        else
        {
            wallClock = clock.GetLocalNow().DateTime;
        }

        return new Placement(
            DestinationLayout.GetFolder(run.Root, run.Request.Organization, wallClock, dayKnown),
            Path.GetFileNameWithoutExtension(DestinationLayout.SanitizeFileName(primary.Asset.FileName)));
    }

    private async Task<IReadOnlyList<(VerifiedCopy Copy, ComponentTransferOutcome Outcome)>> CommitAsync(
        IReadOnlyList<VerifiedCopy> copies,
        Placement placement,
        Run run)
    {
        var results = new List<(VerifiedCopy, ComponentTransferOutcome)>(copies.Count);
        IReadOnlyList<string> targets;
        try
        {
            Directory.CreateDirectory(placement.Folder);
            targets = reservations.Reserve(placement.Folder, placement.BaseName, copies.Select(TargetExtension).ToList());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var error = DestinationErrors.FromException(exception, Directory.Exists(run.Root));
            if (DestinationErrors.AffectsAllFiles(error))
            {
                run.Stop(error);
            }

            foreach (var copy in copies)
            {
                results.Add((copy, await FailAsync(copy.Copy, error).ConfigureAwait(false)));
            }

            return results;
        }

        for (var index = 0; index < copies.Count; index++)
        {
            // Once committing starts it is not cancelled: a rename and an index write are quick,
            // and stopping between them would only leave a Live Photo half-named.
            var outcome = await CommitOneAsync(copies[index], targets[index], placement, run).ConfigureAwait(false);
            results.Add((copies[index], outcome));
        }

        return results;
    }

    private async Task<ComponentTransferOutcome> CommitOneAsync(VerifiedCopy verified, string target, Placement placement, Run run)
    {
        var copy = verified.Copy;
        var partial = copy.PartialPath!;
        try
        {
            ApplyTimestamps(partial, verified.CaptureDate, copy.Component.Asset);

            // File.Move without overwrite fails if something else created the name meanwhile.
            // A just-written file is often opened briefly by antivirus, so sharing violations are retried.
            for (int clash = 1, busy = 0; ; )
            {
                try
                {
                    File.Move(partial, target, overwrite: false);
                    break;
                }
                catch (IOException exception) when (File.Exists(target) && clash < 100)
                {
                    logger.LogDebug(exception, "Destination name was taken at the last moment; choosing another");
                    reservations.Release(target);
                    target = reservations.Reserve(placement.Folder, placement.BaseName, [TargetExtension(verified)], clash++)[0];
                }
                catch (IOException exception) when (exception.HResult is ErrorSharingViolation or ErrorLockViolation && ++busy <= 5)
                {
                    logger.LogDebug(exception, "Copied file is in use; retrying the rename");
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * busy), CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            reservations.Release(target);
            var error = DestinationErrors.FromException(exception, Directory.Exists(run.Root));
            if (DestinationErrors.AffectsAllFiles(error))
            {
                run.Stop(error);
            }

            return await FailAsync(copy, error).ConfigureAwait(false);
        }

        reservations.Release(target);
        run.AddTransferredBytes(copy.Hash!.Length);

        var outcome = new ComponentTransferOutcome(copy.Component, ItemTransferStatus.Transferred, target);
        try
        {
            var info = new FileInfo(target);
            var now = clock.GetUtcNow();
            await mediaRepository.UpsertAsync(
                [
                    new IndexedMediaFile
                    {
                        FullPath = target,
                        MediaType = MediaFormats.GetMediaType(target),
                        FileSize = info.Length,
                        CreatedAt = info.CreationTimeUtc,
                        ModifiedAt = info.LastWriteTimeUtc,
                        CaptureDate = verified.CaptureDate?.Value,
                        Sha256 = copy.Hash.Sha256,
                        FirstSeenAt = now,
                        LastScannedAt = now,
                        IsAvailable = true,
                    },
                ],
                CancellationToken.None).ConfigureAwait(false);
            await transferRepository.CompleteAsync(
                copy.TransferId,
                new TransferOutcomeRecord(TransferStatus.Completed, now, target, copy.Hash.Sha256, copy.Hash.Length),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (DatabaseException exception)
        {
            // The file is verified and in place, and the next destination scan indexes it; but
            // without a working index nothing further is transferred.
            logger.LogError(exception, "Could not record a transferred file in the media index");
            run.Stop(new AppError(ErrorKind.Database, exception.Message));
        }

        logger.LogDebug("Transferred {File}", copy.Component.Asset.FileName);
        return outcome;
    }

    /// <summary>
    /// Gives the file the time it was taken, so it sorts correctly in File Explorer. Done before the
    /// rename, so the index records the final timestamps.
    /// </summary>
    private void ApplyTimestamps(string path, CaptureDate? captureDate, PhotoAsset asset)
    {
        var when = captureDate?.Value ?? asset.CreatedAt ?? asset.ModifiedAt;
        if (when is not { } value)
        {
            return;
        }

        try
        {
            File.SetCreationTimeUtc(path, value.UtcDateTime);
            File.SetLastWriteTimeUtc(path, value.UtcDateTime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            logger.LogDebug(exception, "Could not set file times");
        }
    }

    private static string TargetExtension(VerifiedCopy copy) =>
        copy.Copy.ConvertedToJpeg ? ".JPG" : Path.GetExtension(DestinationLayout.SanitizeFileName(copy.Copy.Component.Asset.FileName));

    // ---------------------------------------------------------------- shared helpers

    private async Task<ComponentTransferOutcome> FailAsync(ComponentCopy copy, AppError error)
    {
        if (copy.PartialPath is { } partial)
        {
            DeletePartial(partial);
        }

        await RecordOutcomeAsync(copy.TransferId, new TransferOutcomeRecord(
            TransferStatus.Failed, clock.GetUtcNow(), ErrorKind: error.Kind.ToString(), ErrorMessage: error.Details)).ConfigureAwait(false);
        return new ComponentTransferOutcome(copy.Component, ItemTransferStatus.Failed, Error: error);
    }

    /// <summary>Drops an item's copies without committing them.</summary>
    private async Task DiscardAsync(CopiedItem item, Run run, AppError? error = null)
    {
        var components = new List<ComponentTransferOutcome>();
        foreach (var component in item.Work.Item.Components)
        {
            var copy = item.Copies.FirstOrDefault(c => c.Component == component);
            if (copy?.PartialPath is { } partial)
            {
                DeletePartial(partial);
                await RecordOutcomeAsync(copy.TransferId, error is null
                    ? new TransferOutcomeRecord(TransferStatus.Cancelled, clock.GetUtcNow())
                    : new TransferOutcomeRecord(TransferStatus.Failed, clock.GetUtcNow(), ErrorKind: error.Kind.ToString(), ErrorMessage: error.Details)).ConfigureAwait(false);
            }

            components.Add(!item.Work.ToCopy.Contains(component)
                ? new ComponentTransferOutcome(component, ItemTransferStatus.AlreadyExists, component.ExistingPath)
                : copy?.Error is { Kind: not ErrorKind.Cancelled } copyError
                    ? new ComponentTransferOutcome(component, ItemTransferStatus.Failed, Error: copyError)
                    : error is not null
                        ? new ComponentTransferOutcome(component, ItemTransferStatus.Failed, Error: error)
                        : new ComponentTransferOutcome(component, ItemTransferStatus.NotAttempted));
        }

        run.Complete(item.Work.Index, new ItemTransferOutcome(item.Work.Item, components));
    }

    /// <summary>Records an outcome; a history write failure must not turn a good copy into a failure.</summary>
    private async Task RecordOutcomeAsync(long transferId, TransferOutcomeRecord outcome)
    {
        if (transferId == 0)
        {
            return;
        }

        try
        {
            await transferRepository.CompleteAsync(transferId, outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DatabaseException exception)
        {
            logger.LogError(exception, "Could not record a transfer outcome");
        }
    }

    private async Task RecordSessionEndAsync(TransferRunResult result)
    {
        try
        {
            var session = await transferRepository.GetSessionAsync(result.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (session is null)
            {
                return;
            }

            await transferRepository.CompleteSessionAsync(
                session with
                {
                    CompletedAt = clock.GetUtcNow(),
                    Status = result.Status,
                    TransferredCount = result.TransferredCount,
                    SkippedCount = result.SkippedCount,
                    FailedCount = result.FailedCount,
                    TransferredBytes = result.BytesTransferred,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (DatabaseException exception)
        {
            logger.LogError(exception, "Could not record the end of the transfer session");
        }
    }

    /// <summary>Deletes one of this service's own temporary files. Never called for any other file.</summary>
    private void DeletePartial(string partialPath)
    {
        try
        {
            File.Delete(partialPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not remove a temporary file");
        }
    }

    private async Task<bool> StartsWithJpegMarkerAsync(string path)
    {
        try
        {
            var header = new byte[3];
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
            return await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false).ConfigureAwait(false) == 3
                && header is [0xFF, 0xD8, 0xFF];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Could not inspect a copied file");
            return false;
        }
    }

    private static bool IsHeif(PhotoAsset asset) => asset.Extension is ".heic" or ".heif" or ".hif";

    /// <summary>Errors after which no further file can succeed.</summary>
    private static bool StopsTheRun(AppError error) =>
        IsDeviceError(error) || DestinationErrors.AffectsAllFiles(error) || error.Kind == ErrorKind.Database;

    private static bool IsDeviceError(AppError error) => error.Kind is
        ErrorKind.DeviceDisconnected or ErrorKind.DeviceNotFound or ErrorKind.DeviceLockedOrUntrusted;

    private static bool IsWorthRetrying(AppError error) => error.Kind is
        ErrorKind.DeviceBusy or ErrorKind.DeviceIo or ErrorKind.Unknown;

    // ---------------------------------------------------------------- run state

    private sealed record ItemWork(int Index, ItemClassification Item, IReadOnlyList<ComponentClassification> ToCopy);

    private sealed record CopiedItem(ItemWork Work, IReadOnlyList<ComponentCopy> Copies);

    private sealed record ComponentCopy(
        ComponentClassification Component,
        long TransferId,
        string? PartialPath,
        HashResult? Hash,
        bool ConvertedToJpeg,
        AppError? Error)
    {
        public static ComponentCopy Copied(ComponentClassification component, long transferId, string partialPath, HashResult hash, bool convertedToJpeg) =>
            new(component, transferId, partialPath, hash, convertedToJpeg, null);

        public static ComponentCopy Failed(ComponentClassification component, long transferId, AppError error) =>
            new(component, transferId, null, null, false, error);
    }

    private sealed record VerifiedCopy(ComponentCopy Copy, CaptureDate? CaptureDate);

    private sealed record Placement(string Folder, string BaseName);

    /// <summary>Shared, thread-safe state of one run: work list, outcomes, counters and progress.</summary>
    private sealed class Run
    {
        private readonly Lock _lock = new();
        private readonly ItemTransferOutcome?[] _outcomes;
        private readonly TimeProvider _clock;
        private readonly IProgress<TransferProgress>? _progress;
        private readonly long _started;
        private readonly TransferRateEstimator _rate = new();
        private long _lastReport = long.MinValue;
        private string? _currentFile;
        private long _bytesRead;
        private long _bytesTransferred;
        private int _itemsDone;
        private int _transferred;
        private int _skipped;
        private int _failed;

        public Run(TransferRequest request, string root, DeviceInfo device, TimeProvider clock, IProgress<TransferProgress>? progress)
        {
            Request = request;
            Root = root;
            Device = device;
            _clock = clock;
            _progress = progress;
            _started = clock.GetTimestamp();
            _outcomes = new ItemTransferOutcome?[request.Items.Count];

            var work = new List<ItemWork>();
            for (var index = 0; index < request.Items.Count; index++)
            {
                var item = request.Items[index];
                var toCopy = item.Components.Where(component => !request.SkipExactDuplicates || component.NeedsTransfer).ToList();
                if (toCopy.Count == 0)
                {
                    _outcomes[index] = new ItemTransferOutcome(
                        item,
                        item.Components.Select(component => new ComponentTransferOutcome(component, ItemTransferStatus.AlreadyExists, component.ExistingPath)).ToList());
                    _skipped++;
                }
                else
                {
                    work.Add(new ItemWork(index, item, toCopy));
                }
            }

            Work = work;
            BytesTotal = work.Sum(w => w.ToCopy.Sum(component => component.Asset.ReportedSize ?? 0));
        }

        public string SessionId { get; } = Guid.NewGuid().ToString("N");

        public TransferRequest Request { get; }

        public string Root { get; }

        public DeviceInfo Device { get; }

        public IReadOnlyList<ItemWork> Work { get; }

        public long BytesTotal { get; }

        public CancellationTokenSource? Stopper { get; set; }

        public AppError? StopReason { get; private set; }

        public long BytesRead
        {
            get
            {
                lock (_lock)
                {
                    return _bytesRead;
                }
            }
        }

        /// <summary>Stops reading further files. The first reason wins.</summary>
        public void Stop(AppError reason)
        {
            lock (_lock)
            {
                StopReason ??= reason;
            }

            try
            {
                Stopper?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The run already finished.
            }
        }

        public void SetCurrentFile(string fileName)
        {
            lock (_lock)
            {
                _currentFile = fileName;
            }

            Report(force: true, currentFile: fileName);
        }

        public void AddBytes(int count)
        {
            lock (_lock)
            {
                _bytesRead += count;
            }

            Report(force: false, currentFile: null);
        }

        /// <summary>Undoes progress from an attempt that is being discarded.</summary>
        public void RewindBytes(long to)
        {
            lock (_lock)
            {
                _bytesRead = Math.Min(_bytesRead, to);
            }
        }

        public void AddTransferredBytes(long count)
        {
            lock (_lock)
            {
                _bytesTransferred += count;
            }
        }

        public void Complete(int index, ItemTransferOutcome outcome)
        {
            lock (_lock)
            {
                _outcomes[index] = outcome;
                _itemsDone++;
                switch (outcome.Status)
                {
                    case ItemTransferStatus.Transferred:
                        _transferred++;
                        break;
                    case ItemTransferStatus.AlreadyExists or ItemTransferStatus.DuplicateFound:
                        _skipped++;
                        break;
                    case ItemTransferStatus.Failed:
                        _failed++;
                        break;
                }
            }

            Report(force: true, currentFile: null);
        }

        public void Report(bool force, string? currentFile)
        {
            if (_progress is null)
            {
                return;
            }

            TransferProgress snapshot;
            lock (_lock)
            {
                var now = _clock.GetTimestamp();
                if (!force && _lastReport != long.MinValue && _clock.GetElapsedTime(_lastReport, now) < ProgressInterval)
                {
                    return;
                }

                _lastReport = now;
                var elapsed = _clock.GetElapsedTime(_started, now);
                _rate.Add(elapsed, _bytesRead);
                snapshot = new TransferProgress(
                    _itemsDone,
                    Work.Count,
                    currentFile ?? _currentFile,
                    _bytesRead,
                    BytesTotal,
                    _rate.BytesPerSecond,
                    _rate.EstimateRemaining(BytesTotal - _bytesRead),
                    _transferred,
                    _skipped,
                    _failed);
            }

            _progress.Report(snapshot);
        }

        public TransferRunResult ToResult(TransferSessionStatus status)
        {
            lock (_lock)
            {
                var items = new List<ItemTransferOutcome>(_outcomes.Length);
                for (var index = 0; index < _outcomes.Length; index++)
                {
                    items.Add(_outcomes[index] ?? NotAttempted(Request.Items[index]));
                }

                _currentFile = null;
                return new TransferRunResult(SessionId, Root, status, items, _bytesTransferred, _clock.GetElapsedTime(_started), StopReason);
            }
        }

        private ItemTransferOutcome NotAttempted(ItemClassification item) => new(
            item,
            item.Components.Select(component => !Request.SkipExactDuplicates || component.NeedsTransfer
                ? new ComponentTransferOutcome(component, ItemTransferStatus.NotAttempted)
                : new ComponentTransferOutcome(component, ItemTransferStatus.AlreadyExists, component.ExistingPath)).ToList());
    }
}
