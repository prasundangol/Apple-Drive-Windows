using AppleDrive.Application.Services;
using AppleDrive.Domain.Results;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.IntegrationTests.Database;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Scanning;

public sealed class DestinationIndexServiceTests : IAsyncLifetime
{
    private readonly TemporaryDirectory _destination = new();
    private TestDatabase _db = null!;
    private DestinationIndexService _service = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _service = new DestinationIndexService(
            new DestinationScanner(NullLogger<DestinationScanner>.Instance),
            _db.Repository,
            TimeProvider.System,
            NullLogger<DestinationIndexService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _destination.Dispose();
    }

    [Fact]
    public async Task First_scan_indexes_supported_media_recursively()
    {
        Write("IMG_0001.HEIC", 10);
        Write(@"2026\09 September\IMG_0002.JPG", 20);
        Write(@"2026\09 September\clip.MOV", 30);
        Write("notes.txt", 5);
        Write("IMG_0001.AAE", 5);
        Write("IMG_0003.HEIC.partial", 5);

        var summary = await SyncAsync();

        Assert.Equal(3, summary.TotalFiles);
        Assert.Equal(3, summary.NewFiles);
        var indexed = await _db.Repository.GetUnderRootAsync(_destination.Path, Ct);
        Assert.Equal(3, indexed.Count);
        Assert.All(indexed, file => Assert.True(file.IsAvailable));
        Assert.Contains(indexed, file => file.FullPath.EndsWith(@"09 September\clip.MOV", StringComparison.Ordinal) && file.FileSize == 30);
    }

    [Fact]
    public async Task Second_scan_reports_everything_unchanged_and_keeps_hashes()
    {
        Write("a.jpg", 10);
        await SyncAsync();
        var record = (await _db.Repository.GetUnderRootAsync(_destination.Path, Ct)).Single();
        await _db.Repository.SetSha256Async(record.Id, new byte[32], Ct);

        var summary = await SyncAsync();

        Assert.Equal(1, summary.UnchangedFiles);
        Assert.Equal(0, summary.NewFiles + summary.ChangedFiles + summary.MissingFiles);
        Assert.NotNull((await _db.Repository.GetByPathAsync(record.FullPath, Ct))!.Sha256);
    }

    [Fact]
    public async Task Modified_file_is_marked_changed_and_its_hash_cleared()
    {
        var path = Write("a.jpg", 10);
        await SyncAsync();
        var record = (await _db.Repository.GetUnderRootAsync(_destination.Path, Ct)).Single();
        await _db.Repository.SetSha256Async(record.Id, new byte[32], Ct);

        File.WriteAllBytes(path, new byte[11]);
        var summary = await SyncAsync();

        Assert.Equal(1, summary.ChangedFiles);
        var stored = await _db.Repository.GetByPathAsync(path, Ct);
        Assert.Equal(11, stored!.FileSize);
        Assert.Null(stored.Sha256);
    }

    [Fact]
    public async Task Deleted_file_is_marked_missing_not_removed()
    {
        var path = Write("a.jpg", 10);
        Write("b.jpg", 10);
        await SyncAsync();

        File.Delete(path);
        var summary = await SyncAsync();

        Assert.Equal(1, summary.MissingFiles);
        var stored = await _db.Repository.GetByPathAsync(path, Ct);
        Assert.NotNull(stored);
        Assert.False(stored.IsAvailable);
    }

    [Fact]
    public async Task Returning_file_becomes_available_again_with_its_hash()
    {
        var path = Write("a.jpg", 10);
        await SyncAsync();
        var record = (await _db.Repository.GetUnderRootAsync(_destination.Path, Ct)).Single();
        await _db.Repository.SetSha256Async(record.Id, new byte[32], Ct);
        var backup = path + ".bak";
        File.Move(path, backup);
        await SyncAsync();

        File.Move(backup, path);
        var summary = await SyncAsync();

        Assert.Equal(1, summary.UnchangedFiles);
        var stored = await _db.Repository.GetByPathAsync(path, Ct);
        Assert.True(stored!.IsAvailable);
        Assert.NotNull(stored.Sha256);
    }

    [Fact]
    public async Task Missing_destination_is_reported_and_leaves_the_index_untouched()
    {
        Write("a.jpg", 10);
        await SyncAsync();
        var records = await _db.Repository.GetUnderRootAsync(_destination.Path, Ct);

        var result = await _service.SyncAsync(_destination.Combine("does-not-exist"), null, Ct);
        Directory.Delete(_destination.Path, recursive: true);
        var gone = await _service.SyncAsync(_destination.Path, null, Ct);

        Assert.Equal(ErrorKind.DestinationUnavailable, result.Error!.Kind);
        Assert.Equal(ErrorKind.DestinationUnavailable, gone.Error!.Kind);
        Assert.All(await _db.Repository.GetUnderRootAsync(_destination.Path, Ct), file => Assert.True(file.IsAvailable));
        Assert.Equal(records.Count, (await _db.Repository.GetUnderRootAsync(_destination.Path, Ct)).Count);
    }

    [Fact]
    public async Task Cancelled_scan_does_not_mark_anything_missing()
    {
        for (var index = 0; index < 50; index++)
        {
            Write($"IMG_{index:0000}.JPG", 1);
        }

        await SyncAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await _service.SyncAsync(_destination.Path, null, cancellation.Token);

        Assert.Equal(ErrorKind.Cancelled, result.Error!.Kind);
        Assert.All(await _db.Repository.GetUnderRootAsync(_destination.Path, Ct), file => Assert.True(file.IsAvailable));
    }

    [Fact]
    public async Task Handles_unicode_and_unusual_file_names()
    {
        Write("写真 #1 (copy).JPG", 1);
        Write("Ünïcödé — été.heic", 1);
        Write("name with  spaces .png", 1);

        var summary = await SyncAsync();

        Assert.Equal(3, summary.NewFiles);
        Assert.NotNull(await _db.Repository.GetByPathAsync(_destination.Combine("写真 #1 (copy).JPG"), Ct));
    }

    [Fact]
    public async Task Trailing_separator_in_root_is_normalized()
    {
        Write("a.jpg", 1);
        await SyncAsync();

        var summary = await _service.SyncAsync(_destination.Path + Path.DirectorySeparatorChar, null, Ct);

        Assert.Equal(1, summary.Value.UnchangedFiles);
    }

    [Fact]
    public async Task Indexes_thousands_of_files()
    {
        for (var folder = 0; folder < 20; folder++)
        {
            for (var index = 0; index < 150; index++)
            {
                Write($@"f{folder}\IMG_{index:0000}.JPG", 1);
            }
        }

        var first = await SyncAsync();
        var second = await SyncAsync();

        Assert.Equal(3_000, first.NewFiles);
        Assert.Equal(3_000, second.UnchangedFiles);
    }

    private async Task<DestinationScanSummary> SyncAsync()
    {
        var result = await _service.SyncAsync(_destination.Path, null, Ct);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }

    private string Write(string relativePath, int size)
    {
        var path = _destination.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
