using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Application.Services;

public enum VisualCheckStage
{
    /// <summary>Fingerprinting destination images that have no stored fingerprint yet.</summary>
    PreparingDestination,

    /// <summary>Comparing new phone images with the destination.</summary>
    ComparingPhotos,
}

public sealed record VisualCheckProgress(VisualCheckStage Stage, int Done, int Total);

/// <summary>
/// Marks new phone images that look like an image already in the destination (resized,
/// recompressed, converted from HEIC to JPEG, renamed) as <see cref="AssetStatus.PossibleDuplicate"/>.
/// These are shown to the user and are never skipped without the user choosing to.
/// </summary>
/// <remarks>
/// <para>Two stages keep this cheap and precise:</para>
/// <list type="number">
/// <item>A 64-bit fingerprint of every destination image (computed once, stored in the index) is
/// compared with the phone image's. For the phone image it comes from the small thumbnail the
/// phone keeps, in all 8 orientations because thumbnails are stored unrotated. JPEGs are read in
/// full instead: testing showed that phones often keep a thumbnail of a different version of
/// saved or edited JPEGs. Anything within <see cref="CandidateDistance"/> bits is a candidate.</item>
/// <item>Candidates are confirmed with a 256-bit fingerprint of both full images. Only a match
/// within <see cref="ConfirmedDistance"/> bits makes a possible duplicate, which rules out chance
/// matches between unrelated photos in a large library.</item>
/// </list>
/// <para>Videos are not compared visually.</para>
/// </remarks>
public sealed class VisualDuplicateDetector(
    IPhonePhotoSource source,
    IMediaRepository repository,
    IPerceptualHashService hasher,
    ILogger<VisualDuplicateDetector> logger)
{
    /// <summary>Differing bits (of 64) for a destination image to be a candidate.</summary>
    public const int CandidateDistance = 10;

    /// <summary>Differing bits (of 256) for a candidate to be confirmed as a possible duplicate.</summary>
    public const int ConfirmedDistance = 36;

    /// <summary>Destination images fingerprinted at once (disk and CPU bound).</summary>
    private const int DestinationConcurrency = 4;

    /// <summary>Best candidates confirmed per phone image.</summary>
    private const int MaxCandidates = 3;

    public async Task<Result<ImportPlan>> CheckAsync(ImportPlan plan, IProgress<VisualCheckProgress>? progress, CancellationToken cancellationToken)
    {
        var candidates = plan.Items
            .SelectMany(item => item.Components.Select(component => (Item: item, Component: component)))
            .Where(pair => pair.Component.Status == AssetStatus.New && pair.Component.Asset.MediaType == MediaType.Image)
            .ToList();
        if (candidates.Count == 0)
        {
            return plan;
        }

        try
        {
            var library = await PrepareDestinationAsync(plan.DestinationRoot, progress, cancellationToken).ConfigureAwait(false);
            if (library.Count == 0)
            {
                logger.LogInformation("Visual check skipped: no fingerprinted images in the destination");
                return plan;
            }

            logger.LogInformation("Visual check started: {Phone} new phone images against {Destination} destination images", candidates.Count, library.Count);
            var replacements = new Dictionary<ItemClassification, ItemClassification>();
            var destinationDetails = new Dictionary<string, DetailHash?>(StringComparer.OrdinalIgnoreCase);
            int notComparable = 0, found = 0, thumbnails = 0, fullReads = 0;
            for (var index = 0; index < candidates.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (item, component) = candidates[index];
                var match = await CompareAsync(component.Asset, library, destinationDetails, cancellationToken).ConfigureAwait(false);
                if (!match.IsSuccess)
                {
                    return match.Error;
                }

                var outcome = match.Value;
                thumbnails += outcome.UsedThumbnail ? 1 : 0;
                fullReads += outcome.ReadFullFile ? 1 : 0;
                if (outcome.Unchecked)
                {
                    notComparable++;
                }
                else if (outcome.SimilarPath is { } similar)
                {
                    found++;
                    var current = replacements.GetValueOrDefault(item, item);
                    var original = current.Components.First(c => c.Asset == component.Asset);
                    replacements[item] = current.With(
                        original,
                        original with { Status = AssetStatus.PossibleDuplicate, SimilarPath = similar, SimilarityDistance = outcome.Distance });
                }

                progress?.Report(new VisualCheckProgress(VisualCheckStage.ComparingPhotos, index + 1, candidates.Count));
            }

            logger.LogInformation(
                "Visual check finished: {Found} possible duplicates, {Unchecked} not comparable, {Thumbnails} phone thumbnails and {Full} full files read",
                found, notComparable, thumbnails, fullReads);
            return plan.WithItems(plan.Items.Select(item => replacements.GetValueOrDefault(item, item)).ToList(), notComparable);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Visual check cancelled");
            return AppError.Cancelled;
        }
        catch (DatabaseException exception)
        {
            logger.LogError(exception, "Media index error during the visual check");
            return new AppError(ErrorKind.Database, exception.Message);
        }
    }

    /// <summary>Fingerprints destination images that don't have one yet (stored), and returns all fingerprinted ones.</summary>
    private async Task<List<(string Path, ulong Hash)>> PrepareDestinationAsync(string root, IProgress<VisualCheckProgress>? progress, CancellationToken cancellationToken)
    {
        var images = (await repository.GetUnderRootAsync(root, cancellationToken).ConfigureAwait(false))
            .Where(file => file.IsAvailable && file.MediaType == MediaType.Image)
            .ToList();
        var missing = images.Where(file => file.PerceptualHash is null).ToList();
        var library = images.Where(file => file.PerceptualHash is not null).Select(file => (file.FullPath, file.PerceptualHash!.Value)).ToList();
        if (missing.Count == 0)
        {
            return library;
        }

        logger.LogInformation("Fingerprinting {Count} destination images", missing.Count);
        var done = 0;
        var failed = 0;
        var gate = new Lock();
        progress?.Report(new VisualCheckProgress(VisualCheckStage.PreparingDestination, 0, missing.Count));
        await Parallel.ForEachAsync(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = DestinationConcurrency, CancellationToken = cancellationToken },
            async (file, token) =>
            {
                ImageFingerprint? fingerprint = null;
                try
                {
                    fingerprint = await hasher.ComputeFileAsync(file.FullPath, token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(exception, "Could not read a destination image to fingerprint it");
                }

                if (fingerprint is not null)
                {
                    await repository.SetPerceptualHashAsync(file.Id, fingerprint.Hash, token).ConfigureAwait(false);
                }

                lock (gate)
                {
                    if (fingerprint is null)
                    {
                        failed++;
                    }
                    else
                    {
                        library.Add((file.FullPath, fingerprint.Hash));
                    }

                    done++;
                    if (done % 25 == 0 || done == missing.Count)
                    {
                        progress?.Report(new VisualCheckProgress(VisualCheckStage.PreparingDestination, done, missing.Count));
                    }
                }
            }).ConfigureAwait(false);

        if (failed > 0)
        {
            logger.LogWarning("{Count} destination images could not be fingerprinted (unreadable, or no decoder for the format)", failed);
        }

        return library;
    }

    private async Task<Result<Comparison>> CompareAsync(
        PhotoAsset asset,
        List<(string Path, ulong Hash)> library,
        Dictionary<string, DetailHash?> destinationDetails,
        CancellationToken cancellationToken)
    {
        // Stage 1: cheap fingerprints of the phone image.
        IReadOnlyList<ulong>? phoneHashes = null;
        ImageFingerprint? full = null;
        var usedThumbnail = false;
        var readFull = false;
        if (!IsJpeg(asset))
        {
            var thumbnail = await ReadAsync(asset, thumbnail: true, stream => hasher.ComputeAllOrientationsAsync(stream, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (thumbnail.Error is { } thumbnailError && IsFatal(thumbnailError))
            {
                return thumbnailError;
            }

            phoneHashes = thumbnail.Value;
            usedThumbnail = phoneHashes is not null;
        }

        if (phoneHashes is null)
        {
            var read = await ReadFullAsync(asset, cancellationToken).ConfigureAwait(false);
            if (read.Error is { } error)
            {
                return IsFatal(error) ? error : Comparison.NotComparable(usedThumbnail, readFull: true);
            }

            full = read.Value;
            readFull = true;
            if (full is null)
            {
                return Comparison.NotComparable(usedThumbnail, readFull);
            }

            phoneHashes = [full.Hash];
        }

        var closest = library
            .Select(entry => (entry.Path, Distance: phoneHashes.Min(hash => ImageFingerprint.Distance(hash, entry.Hash))))
            .Where(entry => entry.Distance <= CandidateDistance)
            .OrderBy(entry => entry.Distance)
            .Take(MaxCandidates)
            .ToList();
        if (closest.Count == 0)
        {
            return Comparison.NoMatch(usedThumbnail, readFull);
        }

        // Stage 2: confirm with the detailed fingerprint of both full images.
        if (full is null)
        {
            var read = await ReadFullAsync(asset, cancellationToken).ConfigureAwait(false);
            if (read.Error is { } error)
            {
                return IsFatal(error) ? error : Comparison.NotComparable(usedThumbnail, readFull: true);
            }

            full = read.Value;
            readFull = true;
            if (full is null)
            {
                return Comparison.NotComparable(usedThumbnail, readFull);
            }
        }

        (string Path, int Distance)? best = null;
        foreach (var (path, _) in closest)
        {
            if (!destinationDetails.TryGetValue(path, out var detail))
            {
                detail = await DestinationDetailAsync(path, cancellationToken).ConfigureAwait(false);
                destinationDetails[path] = detail;
            }

            if (detail is { } destination && full.Detail.DistanceTo(destination) is var distance && distance <= ConfirmedDistance
                && (best is null || distance < best.Value.Distance))
            {
                best = (path, distance);
            }
        }

        return best is { } confirmed
            ? new Comparison(confirmed.Path, confirmed.Distance, Unchecked: false, usedThumbnail, readFull)
            : Comparison.NoMatch(usedThumbnail, readFull);
    }

    private async Task<DetailHash?> DestinationDetailAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return (await hasher.ComputeFileAsync(path, cancellationToken).ConfigureAwait(false))?.Detail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(exception, "Could not read a destination image to confirm a visual match");
            return null;
        }
    }

    private Task<(ImageFingerprint? Value, AppError? Error)> ReadFullAsync(PhotoAsset asset, CancellationToken cancellationToken) =>
        ReadAsync(asset, thumbnail: false, stream => hasher.ComputeAsync(stream, cancellationToken), cancellationToken);

    /// <summary>Opens the phone file or its thumbnail and hashes it. Device errors are returned, not thrown.</summary>
    private async Task<(T? Value, AppError? Error)> ReadAsync<T>(PhotoAsset asset, bool thumbnail, Func<Stream, Task<T?>> hash, CancellationToken cancellationToken)
        where T : class
    {
        var open = thumbnail
            ? await source.OpenThumbnailAsync(asset, cancellationToken).ConfigureAwait(false)
            : await source.OpenAssetAsync(asset, cancellationToken).ConfigureAwait(false);
        if (!open.IsSuccess)
        {
            return (null, open.Error);
        }

        try
        {
            await using var stream = open.Value;
            return (await hash(stream).ConfigureAwait(false), null);
        }
        catch (SourceReadException exception)
        {
            return (null, exception.Error);
        }
        catch (IOException exception)
        {
            return (null, new AppError(ErrorKind.DeviceIo, exception.Message, exception.HResult));
        }
    }

    private static bool IsJpeg(PhotoAsset asset) => asset.Extension is ".jpg" or ".jpeg";

    /// <summary>The phone is gone or cancelled: stop rather than fail every remaining image.</summary>
    private static bool IsFatal(AppError error) => error.Kind is
        ErrorKind.Cancelled or ErrorKind.DeviceDisconnected or ErrorKind.DeviceNotFound or ErrorKind.DeviceLockedOrUntrusted;

    private sealed record Comparison(string? SimilarPath, int? Distance, bool Unchecked, bool UsedThumbnail, bool ReadFullFile)
    {
        public static Comparison NoMatch(bool usedThumbnail, bool readFull) => new(null, null, false, usedThumbnail, readFull);

        public static Comparison NotComparable(bool usedThumbnail, bool readFull) => new(null, null, true, usedThumbnail, readFull);
    }
}
