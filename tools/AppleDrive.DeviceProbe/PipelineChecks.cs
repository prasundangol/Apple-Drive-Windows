using System.Diagnostics;
using System.Security.Cryptography;
using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Application.Settings;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Infrastructure.Database;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Hashing;
using AppleDrive.Infrastructure.Imaging;
using AppleDrive.Infrastructure.Iphone;
using AppleDrive.Infrastructure.Metadata;
using Microsoft.Data.Sqlite;

namespace AppleDrive.Tools.DeviceProbe;

/// <summary>Checks that run the real application services against the connected phone.</summary>
internal static class PipelineChecks
{
    /// <summary>
    /// Reads files, reconnects, and reads the same object ids again. The transfer engine re-reads
    /// a suspicious file after reconnecting, which is only sound if ids survive a reconnect.
    /// </summary>
    public static async Task<int> ReconnectCheckAsync(WpdPhotoSource source, DeviceInfo device, IReadOnlyList<PhotoAsset> assets, int count)
    {
        Console.WriteLine();
        Console.WriteLine($"== Reconnect check: {count} files read before and after reconnecting ==");
        var hashes = new Sha256HashService();
        var sample = assets.Where(a => a.ReportedSize is > 0).OrderBy(a => a.ReportedSize).Take(count).ToList();
        var before = new Dictionary<string, HashResult>();
        foreach (var asset in sample)
        {
            before[asset.Id] = await HashAsync(source, hashes, asset);
        }

        var reconnect = await source.ConnectAsync(device, CancellationToken.None);
        Console.WriteLine($"Reconnected: {reconnect.Status}");
        var enumerated = await source.EnumerateAssetsAsync(null, CancellationToken.None);
        var sameIds = enumerated.IsSuccess && sample.All(a => enumerated.Value.Any(e => e.Id == a.Id && e.FileName == a.FileName));
        Console.WriteLine($"Same object ids and names after reconnecting: {sameIds}");

        var mismatches = 0;
        foreach (var asset in sample)
        {
            var after = await HashAsync(source, hashes, asset);
            var same = after.Matches(before[asset.Id].Sha256) && after.Length == before[asset.Id].Length;
            mismatches += same ? 0 : 1;
            Console.WriteLine($"  {asset.FileName} ({asset.Id}): {(same ? "same content" : "DIFFERENT CONTENT")}");
        }

        Console.WriteLine($"Content mismatches after reconnect: {mismatches} of {sample.Count}");
        return mismatches == 0 && sameIds ? 0 : 5;
    }

    /// <summary>
    /// Analysis and transfer into <paramref name="destination"/> with the production services,
    /// using a temporary index database (never the app's own).
    /// </summary>
    public static async Task<int> TransferAsync(WpdPhotoSource source, string destination, FolderOrganization organization, int limit, bool verbose)
    {
        Console.WriteLine();
        Console.WriteLine($"== Transfer into {destination} ({organization}, at most {limit} items) ==");
        var databaseFile = Path.Combine(Path.GetTempPath(), $"appledrive-probe-{Guid.NewGuid():N}.db");
        try
        {
            var database = new SqliteDatabase(databaseFile);
            await new DatabaseMigrator(database, new ConsoleLogger<DatabaseMigrator>()).MigrateAsync(CancellationToken.None);
            var media = new MediaRepository(database);
            var history = new TransferRepository(database);
            var hashes = new Sha256HashService();
            var lookup = new DestinationContentLookup(media, hashes, new ConsoleLogger<DestinationContentLookup>());
            var index = new DestinationIndexService(new DestinationScanner(new ConsoleLogger<DestinationScanner>()), media, TimeProvider.System, new ConsoleLogger<DestinationIndexService>());
            var detector = new ExactDuplicateDetector(source, lookup, history, hashes, new ConsoleLogger<ExactDuplicateDetector>());
            var service = new MediaTransferService(
                source, hashes, new CaptureDateReader(), lookup, media, history, new DestinationNameReservations(), TimeProvider.System, new ConsoleLogger<MediaTransferService>());

            var watch = Stopwatch.StartNew();
            var sync = await index.SyncAsync(destination, null, CancellationToken.None);
            if (!sync.IsSuccess)
            {
                Console.WriteLine($"Destination scan failed: {sync.Error}");
                return 6;
            }

            var assets = await source.EnumerateAssetsAsync(null, CancellationToken.None);
            if (!assets.IsSuccess)
            {
                Console.WriteLine($"Enumeration failed: {assets.Error}");
                return 6;
            }

            // Smallest items first, keeping every format and Live Photos in the sample.
            var items = LivePhotoGrouper.Group(assets.Value)
                .GroupBy(item => (item.Primary.Extension, item.IsLivePhoto))
                .SelectMany(g => g.OrderBy(item => item.Components.Sum(c => c.ReportedSize ?? 0)).Select((item, rank) => (item, rank)))
                .OrderBy(pair => pair.rank)
                .Select(pair => pair.item)
                .Take(limit)
                .ToList();
            var plan = await detector.ClassifyAsync(items, destination, null, CancellationToken.None);
            if (!plan.IsSuccess)
            {
                Console.WriteLine($"Duplicate check failed: {plan.Error}");
                return 6;
            }

            Console.WriteLine($"Plan: {plan.Value.NewCount} new, {plan.Value.ExactDuplicateCount} duplicates, {plan.Value.TransferBytes:N0} bytes ({watch.Elapsed.TotalSeconds:F1} s)");

            TransferProgress? last = null;
            var progress = new Progress<TransferProgress>(p => last = p);
            watch.Restart();
            var result = await service.TransferAsync(new TransferRequest(plan.Value.Items, destination, organization), progress, CancellationToken.None);
            if (!result.IsSuccess)
            {
                Console.WriteLine($"Transfer failed: {result.Error}");
                return 7;
            }

            var run = result.Value;
            var seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);
            Console.WriteLine($"Result: {run.Status} in {seconds:F1} s | transferred {run.TransferredCount}, skipped {run.SkippedCount}, failed {run.FailedCount}, not attempted {run.NotAttemptedCount}");
            Console.WriteLine($"Bytes: {run.BytesTransferred:N0} ({run.BytesTransferred / seconds / 1_000_000:F1} MB/s) | last speed report {last?.BytesPerSecond / 1_000_000:F1} MB/s");
            if (run.StopReason is { } reason)
            {
                Console.WriteLine($"Stopped: {reason}");
            }

            foreach (var item in run.FailedItems)
            {
                Console.WriteLine($"  FAILED {item.FileName}: {item.Error}");
            }

            foreach (var item in run.Items.Where(i => i.Status == ItemTransferStatus.Transferred).Take(verbose ? int.MaxValue : 12))
            {
                foreach (var component in item.Components)
                {
                    var asset = component.Component.Asset;
                    Console.WriteLine(verbose
                        ? $"  {asset.SourcePath} (phone date {asset.CreatedAt?.ToString("u") ?? "none"}) -> {Path.GetRelativePath(destination, component.DestinationPath ?? "?")}"
                        : $"  {asset.FileName,-14} -> {Path.GetRelativePath(destination, component.DestinationPath ?? "?")}");
                }
            }

            var partials = Directory.GetFiles(destination, "*" + MediaTransferService.PartialFileSuffix, SearchOption.AllDirectories);
            var inProgress = await history.GetInProgressAsync(CancellationToken.None);
            Console.WriteLine($"Leftover .partial files: {partials.Length}, unfinished history records: {inProgress.Count}");
            return run.FailedCount == 0 && partials.Length == 0 ? 0 : 8;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(databaseFile) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// Compares the visual fingerprint of each image's device thumbnail with that of the full
    /// file, to see whether thumbnails can stand in for full reads when looking for similar photos.
    /// </summary>
    public static async Task<int> ThumbnailCheckAsync(WpdPhotoSource source, IReadOnlyList<PhotoAsset> assets, int count)
    {
        Console.WriteLine();
        Console.WriteLine($"== Thumbnail check: {count} images, thumbnail vs full file ==");
        var hasher = new WicPerceptualHashService(new ConsoleLogger<WicPerceptualHashService>());
        var sample = assets
            .Where(a => a.MediaType == MediaType.Image)
            .GroupBy(a => a.Extension)
            .SelectMany(g => g.Select((asset, rank) => (asset, rank)))
            .OrderBy(pair => pair.rank)
            .Select(pair => pair.asset)
            .Take(count)
            .ToList();
        var distances = new List<int>();
        var thumbWatch = new Stopwatch();
        var fullWatch = new Stopwatch();
        foreach (var asset in sample)
        {
            thumbWatch.Start();
            var thumbOpen = await source.OpenThumbnailAsync(asset, CancellationToken.None);
            IReadOnlyList<ulong>? thumb = null;
            long thumbBytes = 0;
            if (thumbOpen.IsSuccess)
            {
                await using var stream = thumbOpen.Value;
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                thumbBytes = buffer.Length;
                buffer.Position = 0;
                thumb = await hasher.ComputeAllOrientationsAsync(buffer, CancellationToken.None);
            }

            thumbWatch.Stop();
            fullWatch.Start();
            var fullOpen = await source.OpenAssetAsync(asset, CancellationToken.None);
            ImageFingerprint? full = null;
            if (fullOpen.IsSuccess)
            {
                await using var stream = fullOpen.Value;
                full = await hasher.ComputeAsync(stream, CancellationToken.None);
            }

            fullWatch.Stop();
            var distance = thumb is not null && full is not null ? thumb.Min(hash => ImageFingerprint.Distance(hash, full.Hash)) : (int?)null;
            if (distance is { } d)
            {
                distances.Add(d);
            }

            Console.WriteLine($"  {asset.FileName,-14} thumb {(thumbOpen.IsSuccess ? $"{thumbBytes,7:N0} B" : $"none ({thumbOpen.Error!.Kind})"),-26} full {(full is null ? "not decoded" : $"{full.Width}x{full.Height}"),-12} distance {distance?.ToString() ?? "-"}");
        }

        Console.WriteLine($"Distances: {string.Join(", ", distances.Order())}");
        Console.WriteLine($"Time: thumbnails {thumbWatch.Elapsed.TotalSeconds:F1} s, full files {fullWatch.Elapsed.TotalSeconds:F1} s");
        return 0;
    }

    /// <summary>
    /// Real-data check of the visual duplicate detector. The destination holds copies of the phone's
    /// photos (use --transfer first); every phone image is then treated as new, so each should find
    /// its own copy. Matches to a different photo are reported: they are the false-positive rate.
    /// </summary>
    public static async Task<int> VisualCheckAsync(WpdPhotoSource source, string destination)
    {
        Console.WriteLine();
        Console.WriteLine($"== Visual check against {destination} ==");
        var databaseFile = Path.Combine(Path.GetTempPath(), $"appledrive-probe-{Guid.NewGuid():N}.db");
        try
        {
            var database = new SqliteDatabase(databaseFile);
            await new DatabaseMigrator(database, new ConsoleLogger<DatabaseMigrator>()).MigrateAsync(CancellationToken.None);
            var media = new MediaRepository(database);
            var history = new TransferRepository(database);
            var hashes = new Sha256HashService();
            var lookup = new DestinationContentLookup(media, hashes, new ConsoleLogger<DestinationContentLookup>());
            await new DestinationIndexService(new DestinationScanner(new ConsoleLogger<DestinationScanner>()), media, TimeProvider.System, new ConsoleLogger<DestinationIndexService>())
                .SyncAsync(destination, null, CancellationToken.None);
            var assets = await source.EnumerateAssetsAsync(null, CancellationToken.None);
            var items = LivePhotoGrouper.Group(assets.Value);

            // Everything "new", so every image is compared visually.
            var plan = new ImportPlan(
                DestinationIndexService.NormalizeRoot(destination),
                items.Select(item => new ItemClassification(item, item.Components.Select(c => new ComponentClassification(c, AssetStatus.New)).ToList())).ToList(),
                0);
            var visual = new VisualDuplicateDetector(source, media, new WicPerceptualHashService(new ConsoleLogger<WicPerceptualHashService>()), new ConsoleLogger<VisualDuplicateDetector>());
            var watch = Stopwatch.StartNew();
            var result = await visual.CheckAsync(plan, null, CancellationToken.None);
            if (!result.IsSuccess)
            {
                Console.WriteLine($"Visual check failed: {result.Error}");
                return 9;
            }

            var images = result.Value.Items.SelectMany(i => i.Components).Where(c => c.Asset.MediaType == MediaType.Image).ToList();
            var own = images.Count(c => c.SimilarPath is { } p && string.Equals(Path.GetFileName(p), c.Asset.FileName, StringComparison.OrdinalIgnoreCase));
            var other = images.Where(c => c.SimilarPath is { } p && !string.Equals(Path.GetFileName(p), c.Asset.FileName, StringComparison.OrdinalIgnoreCase)).ToList();
            var missed = images.Where(c => c.SimilarPath is null).ToList();
            Console.WriteLine($"{images.Count} phone images in {watch.Elapsed.TotalSeconds:F1} s: {own} matched their own copy, {other.Count} matched a different file, {missed.Count} no match, {result.Value.VisuallyUncheckedImages} not comparable");
            foreach (var component in other)
            {
                Console.WriteLine($"  {component.Asset.FileName} ~ {Path.GetFileName(component.SimilarPath)} (256-bit distance {component.SimilarityDistance})");
            }

            foreach (var component in missed.Take(10))
            {
                Console.WriteLine($"  no match: {component.Asset.FileName}");
            }

            return 0;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(databaseFile) + "*"))
            {
                File.Delete(file);
            }
        }
    }

    /// <summary>
    /// Compares each image's EXIF orientation with the orientation in which its device thumbnail
    /// matches the upright photo, re-reads every file to confirm the content is stable, and reports
    /// what the device offers as video thumbnails.
    /// </summary>
    public static async Task<int> OrientationCheckAsync(WpdPhotoSource source, IReadOnlyList<PhotoAsset> assets, int count)
    {
        Console.WriteLine();
        Console.WriteLine($"== Orientation check: {count} images ==");
        var hasher = new WicPerceptualHashService(new ConsoleLogger<WicPerceptualHashService>());
        var sha = new Sha256HashService();
        var images = assets.Where(a => a.MediaType == MediaType.Image).Take(count).ToList();
        var pairs = new Dictionary<(int? Exif, int Best), int>();
        var firstHashes = new Dictionary<string, byte[]>();
        foreach (var asset in images)
        {
            var thumbOpen = await source.OpenThumbnailAsync(asset, CancellationToken.None);
            IReadOnlyList<ulong>? thumb = null;
            if (thumbOpen.IsSuccess)
            {
                await using var stream = thumbOpen.Value;
                thumb = await hasher.ComputeAllOrientationsAsync(stream, CancellationToken.None);
            }

            var fullOpen = await source.OpenAssetAsync(asset, CancellationToken.None);
            if (!fullOpen.IsSuccess)
            {
                continue;
            }

            byte[] content;
            await using (var stream = fullOpen.Value)
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                content = buffer.ToArray();
            }

            firstHashes[asset.Id] = SHA256.HashData(content);
            // Never close a device stream early (it upsets the phone): the orientation comes from the full read.
            var exif = CaptureDateReader.ReadOrientation(new MemoryStream(content));
            var full = await hasher.ComputeAsync(new MemoryStream(content), CancellationToken.None);
            if (thumb is null || full is null)
            {
                continue;
            }

            var distances = thumb.Select(hash => ImageFingerprint.Distance(hash, full.Hash)).ToList();
            var best = distances.IndexOf(distances.Min());
            var key = (exif, distances.Min() <= 10 ? best : -1);
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
        }

        Console.WriteLine("EXIF orientation → best thumbnail orientation index (rotation×2 + mirrored; -1 = no match): count");
        foreach (var ((exifValue, best), n) in pairs.OrderBy(p => p.Key.Exif).ThenBy(p => p.Key.Best))
        {
            Console.WriteLine($"  {exifValue?.ToString() ?? "none"} → {best}: {n}");
        }


        var mismatches = 0;
        foreach (var asset in images.Where(a => firstHashes.ContainsKey(a.Id)))
        {
            var again = await HashAsync(source, sha, asset);
            if (!again.Matches(firstHashes[asset.Id]))
            {
                mismatches++;
                Console.WriteLine($"  CONTENT CHANGED on re-read: {asset.FileName}");
            }
        }

        Console.WriteLine($"Re-read after early closes: {mismatches} content mismatches of {firstHashes.Count}");

        Console.WriteLine("Video thumbnails:");
        foreach (var video in assets.Where(a => a.MediaType == MediaType.Video).Take(8))
        {
            var open = await source.OpenThumbnailAsync(video, CancellationToken.None);
            if (!open.IsSuccess)
            {
                Console.WriteLine($"  {video.FileName}: none ({open.Error!.Kind})");
                continue;
            }

            await using var stream = open.Value;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;
            var fingerprint = await hasher.ComputeAsync(buffer, CancellationToken.None);
            Console.WriteLine($"  {video.FileName}: {buffer.Length:N0} B, {(fingerprint is null ? "not decodable" : $"{fingerprint.Width}x{fingerprint.Height}")}");
        }

        return mismatches == 0 ? 0 : 10;
    }

    /// <summary>
    /// What happens to the next read after a device stream is closed before its end: repeated
    /// "read the first 64 KB of one file, then read another file in full" cycles.
    /// </summary>
    public static async Task<int> EarlyCloseCheckAsync(WpdPhotoSource source, DeviceInfo device, IReadOnlyList<PhotoAsset> assets, bool drain)
    {
        Console.WriteLine();
        Console.WriteLine($"== Early-close check ({(drain ? "reading the rest before closing" : "closing after 64 KB")}) ==");
        var sha = new Sha256HashService();
        var files = assets.Where(a => a.ReportedSize is > 300_000 and < 5_000_000).Take(12).ToList();
        var reference = new Dictionary<string, HashResult>();
        foreach (var asset in files)
        {
            reference[asset.Id] = await HashAsync(source, sha, asset);
        }

        var failures = 0;
        for (var i = 0; i + 1 < files.Count; i += 2)
        {
            var open = await source.OpenAssetAsync(files[i], CancellationToken.None);
            if (!open.IsSuccess)
            {
                Console.WriteLine($"  open {files[i].FileName}: {open.Error}");
                failures++;
                continue;
            }

            await using (var stream = open.Value)
            {
                var buffer = new byte[64 * 1024];
                await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
                if (drain)
                {
                    await stream.CopyToAsync(Stream.Null);
                }
            }

            try
            {
                var next = await HashAsync(source, sha, files[i + 1]);
                var same = next.Matches(reference[files[i + 1].Id].Sha256);
                Console.WriteLine($"  after closing {files[i].FileName}: {files[i + 1].FileName} {(same ? "read correctly" : "WRONG CONTENT")}");
                failures += same ? 0 : 1;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException)
            {
                Console.WriteLine($"  after closing {files[i].FileName}: {files[i + 1].FileName} failed: {exception.Message}");
                failures++;
                await source.ConnectAsync(device, CancellationToken.None);
            }
        }

        Console.WriteLine($"Failures: {failures}");
        return failures == 0 ? 0 : 11;
    }

    /// <summary>Makes previews of the named phone files with the real thumbnail service, into a throwaway cache.</summary>
    public static async Task<int> PreviewCheckAsync(WpdPhotoSource source, IReadOnlyList<PhotoAsset> assets, IReadOnlyList<string> names)
    {
        Console.WriteLine();
        Console.WriteLine($"== Preview check: {string.Join(", ", names)} ==");
        var cache = Path.Combine(Path.GetTempPath(), $"appledrive-previews-{Guid.NewGuid():N}");
        var settings = new ProbeSettings(cache);
        using var service = new ThumbnailService(source, settings, new AppPaths(cache), new ConsoleLogger<ThumbnailService>());
        try
        {
            foreach (var name in names)
            {
                foreach (var asset in assets.Where(a => string.Equals(a.FileName, name, StringComparison.OrdinalIgnoreCase)))
                {
                    var watch = Stopwatch.StartNew();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    try
                    {
                        var preview = await service.GetPhoneThumbnailAsync(asset, timeout.Token);
                        Console.WriteLine($"  {asset.SourcePath}: {(preview is null ? "no preview" : $"{new FileInfo(preview).Length:N0} B")} in {watch.ElapsedMilliseconds} ms");
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"  {asset.SourcePath}: TIMED OUT after {watch.ElapsedMilliseconds} ms");
                        return 12;
                    }
                }
            }

            return 0;
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    private sealed class ProbeSettings(string cache) : ISettingsService
    {
        public AppSettings Current { get; } = new() { ThumbnailCacheFolder = cache };

        public event EventHandler<AppSettings>? SettingsChanged
        {
            add { }
            remove { }
        }

        public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static async Task<HashResult> HashAsync(IPhonePhotoSource source, IHashService hashes, PhotoAsset asset)
    {
        var open = await source.OpenAssetAsync(asset, CancellationToken.None);
        if (!open.IsSuccess)
        {
            throw new InvalidOperationException($"Could not open {asset.FileName}: {open.Error}");
        }

        await using var stream = open.Value;
        return await hashes.ComputeAsync(stream, null, CancellationToken.None);
    }
}
