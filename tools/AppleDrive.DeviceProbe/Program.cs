// Diagnostic tool: proves the Windows -> iPhone path end to end without the UI.
// Lists portable devices, connects to the first iPhone, enumerates media, and reads a few
// files fully (in memory; with --copy-to they are also saved to that folder, never overwriting).
// Nothing on the phone is ever changed.
//
// Usage: AppleDrive.DeviceProbe [--read <count>] [--copy-to <folder>]
//                               [--reconnect-check <count>] [--thumbnails <count>] [--visual-check <folder>]
//                               [--orientation-check <count>]
//                               [--transfer <folder> [--organize flat|month|day] [--limit <items>]]
//
// --transfer runs the real analysis and transfer engine into <folder>, with a temporary index
// database (the app's own index is never touched).

using System.Diagnostics;
using System.Security.Cryptography;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Infrastructure.Iphone;
using AppleDrive.Infrastructure.Iphone.Wpd;
using AppleDrive.Tools.DeviceProbe;

var readCount = 3;
string? copyTo = null;
string? transferTo = null;
var organization = FolderOrganization.YearMonth;
var limit = 40;
var reconnectCount = 0;
var thumbnailCount = 0;
string? visualCheck = null;
var orientationCount = 0;
string[]? previewNames = null;
var earlyClose = args.Contains("--early-close-check");
var earlyCloseDrain = args.Contains("--early-close-drain");
for (var index = 0; index < args.Length - 1; index++)
{
    if (args[index] == "--read" && int.TryParse(args[index + 1], out var parsed))
    {
        readCount = Math.Max(0, parsed);
    }

    if (args[index] == "--copy-to")
    {
        copyTo = Directory.CreateDirectory(args[index + 1]).FullName;
    }

    if (args[index] == "--transfer")
    {
        transferTo = Directory.CreateDirectory(args[index + 1]).FullName;
    }

    if (args[index] == "--organize")
    {
        organization = args[index + 1] switch { "flat" => FolderOrganization.Flat, "day" => FolderOrganization.YearMonthDay, _ => FolderOrganization.YearMonth };
    }

    if (args[index] == "--limit" && int.TryParse(args[index + 1], out var itemLimit))
    {
        limit = Math.Max(1, itemLimit);
    }

    if (args[index] == "--orientation-check" && int.TryParse(args[index + 1], out var orientations))
    {
        orientationCount = Math.Max(1, orientations);
    }

    if (args[index] == "--preview")
    {
        previewNames = args[index + 1].Split(',');
    }

    if (args[index] == "--visual-check")
    {
        visualCheck = Directory.CreateDirectory(args[index + 1]).FullName;
    }

    if (args[index] == "--thumbnails" && int.TryParse(args[index + 1], out var thumbs))
    {
        thumbnailCount = Math.Max(1, thumbs);
    }

    if (args[index] == "--reconnect-check" && int.TryParse(args[index + 1], out var reconnect))
    {
        reconnectCount = Math.Max(1, reconnect);
    }
}

Console.WriteLine("== Portable devices ==");
var allDevices = WpdDeviceManager.GetAllDevices();
if (allDevices.Count == 0)
{
    Console.WriteLine("No portable devices found. Is the iPhone plugged in, unlocked, and trusted?");
    return 1;
}

foreach (var device in allDevices)
{
    Console.WriteLine($"- {device.FriendlyName} | {device.Manufacturer} | Apple={WpdDeviceManager.IsAppleDevice(device)}");
    Console.WriteLine($"  {device.Id}");
}

var iphone = allDevices.FirstOrDefault(WpdDeviceManager.IsAppleDevice);
if (iphone is null)
{
    Console.WriteLine("No Apple device found.");
    return 1;
}

await using var source = new WpdPhotoSource(new ConsoleLogger<WpdPhotoSource>());

Console.WriteLine();
Console.WriteLine($"== Connecting to {iphone.FriendlyName} ==");
var connectWatch = Stopwatch.StartNew();
var connection = await source.ConnectAsync(iphone, CancellationToken.None);
Console.WriteLine($"Status: {connection.Status} ({connectWatch.ElapsedMilliseconds} ms) {connection.Error}");
if (connection.Status != DeviceConnectionStatus.Connected)
{
    return 2;
}

Console.WriteLine();
Console.WriteLine("== Enumerating ==");
var enumerateWatch = Stopwatch.StartNew();
var progress = new Progress<int>(count =>
{
    if (count % 500 == 0)
    {
        Console.WriteLine($"  ... {count} files ({enumerateWatch.Elapsed:mm\\:ss})");
    }
});
var assetsResult = await source.EnumerateAssetsAsync(progress, CancellationToken.None);
if (!assetsResult.IsSuccess)
{
    Console.WriteLine($"Enumeration failed: {assetsResult.Error}");
    return 3;
}

var assets = assetsResult.Value;
var items = LivePhotoGrouper.Group(assets);
Console.WriteLine($"Files: {assets.Count} in {enumerateWatch.Elapsed:mm\\:ss\\.f}");
Console.WriteLine($"Items: {items.Count} (photos {items.Count(i => i.MediaType == MediaType.Image)}, " +
                  $"live {items.Count(i => i.IsLivePhoto)}, videos {items.Count(i => i.MediaType == MediaType.Video)})");
Console.WriteLine($"Reported bytes: {assets.Sum(a => a.ReportedSize ?? 0):N0}");
Console.WriteLine($"Missing size: {assets.Count(a => a.ReportedSize is null)}, missing date: {assets.Count(a => a.CreatedAt is null)}, " +
                  $"with dimensions: {assets.Count(a => a.Width is not null)}, with persistent id: {assets.Count(a => a.PersistentId is not null)}");

Console.WriteLine("By extension:");
foreach (var group in assets.GroupBy(a => a.Extension).OrderByDescending(g => g.Count()))
{
    Console.WriteLine($"  {group.Key,-6} {group.Count()}");
}

Console.WriteLine("By folder:");
foreach (var group in assets.GroupBy(a => a.SourceFolder).OrderBy(g => g.Key).Take(20))
{
    Console.WriteLine($"  {group.Key} ({group.Count()})");
}

Console.WriteLine("Sample:");
foreach (var asset in assets.Take(8))
{
    Console.WriteLine($"  {asset.SourcePath} | {asset.ReportedSize:N0} B | created {asset.CreatedAt:u} | " +
                      $"{asset.Width}x{asset.Height} | pid {asset.PersistentId}");
}

if (readCount > 0)
{
    Console.WriteLine();
    Console.WriteLine($"== Reading up to {readCount} file(s) fully (memory only) ==");
    // Round-robin across extensions so every format is exercised; smallest files first.
    var toRead = assets
        .GroupBy(a => a.Extension)
        .SelectMany(g => g.OrderBy(a => a.ReportedSize ?? long.MaxValue).Select((asset, rank) => (asset, rank)))
        .OrderBy(pair => pair.rank)
        .Select(pair => pair.asset)
        .Take(readCount)
        .ToList();
    var mismatches = 0;

    foreach (var asset in toRead)
    {
        var watch = Stopwatch.StartNew();
        var open = await source.OpenAssetAsync(asset, CancellationToken.None);
        if (!open.IsSuccess)
        {
            Console.WriteLine($"  {asset.FileName}: open failed {open.Error}");
            continue;
        }

        await using var stream = open.Value;
        await using var copy = copyTo is null
            ? null
            : new FileStream(Path.Combine(copyTo, asset.FileName), FileMode.CreateNew, FileAccess.Write);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[stream is WpdReadStream wpd ? wpd.OptimalBufferSize : 256 * 1024];
        var header = new byte[16];
        long total = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                if (total < header.Length)
                {
                    buffer.AsSpan(0, (int)Math.Min(read, header.Length - total)).CopyTo(header.AsSpan((int)total));
                }

                sha.AppendData(buffer, 0, read);
                if (copy is not null)
                {
                    await copy.WriteAsync(buffer.AsMemory(0, read));
                }
                total += read;
            }
        }
        catch (IOException exception)
        {
            Console.WriteLine($"  {asset.FileName}: read failed after {total:N0} bytes: {exception.Message}");
            continue;
        }

        if (total != asset.ReportedSize)
        {
            mismatches++;
        }

        var seconds = Math.Max(watch.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"  {asset.FileName}: read {total:N0} B (reported {asset.ReportedSize:N0}) " +
                          $"{(total == asset.ReportedSize ? "size OK" : "SIZE DIFFERS")} | " +
                          $"{total / seconds / 1024 / 1024:F1} MB/s | magic {Convert.ToHexString(header, 0, 12)} | " +
                          $"sha256 {Convert.ToHexString(sha.GetHashAndReset())[..16]}...");
    }

    Console.WriteLine($"Size mismatches: {mismatches} of {toRead.Count}");
}

if (reconnectCount > 0)
{
    var check = await PipelineChecks.ReconnectCheckAsync(source, iphone, assets, reconnectCount);
    if (check != 0)
    {
        return check;
    }
}

if (thumbnailCount > 0)
{
    await PipelineChecks.ThumbnailCheckAsync(source, assets, thumbnailCount);
}

if (earlyClose || earlyCloseDrain)
{
    return await PipelineChecks.EarlyCloseCheckAsync(source, iphone, assets, drain: earlyCloseDrain);
}

if (previewNames is not null)
{
    return await PipelineChecks.PreviewCheckAsync(source, assets, previewNames);
}

if (orientationCount > 0)
{
    return await PipelineChecks.OrientationCheckAsync(source, assets, orientationCount);
}

if (visualCheck is not null)
{
    return await PipelineChecks.VisualCheckAsync(source, visualCheck);
}

if (transferTo is not null)
{
    return await PipelineChecks.TransferAsync(source, transferTo, organization, limit, args.Contains("--verbose"));
}

Console.WriteLine();
Console.WriteLine(copyTo is null ? "Done. Nothing was written or modified." : $"Done. Copies written to {copyTo}; nothing on the phone was modified.");
return 0;
