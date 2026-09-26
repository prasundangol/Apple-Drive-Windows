using System.Security.Cryptography;
using AppleDrive.Application.Services;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Domain.Results;
using AppleDrive.Infrastructure.Database;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Hashing;
using AppleDrive.IntegrationTests.Database;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Duplicates;

public sealed class ExactDuplicateDetectorTests : IAsyncLifetime
{
    private readonly TemporaryDirectory _destination = new();
    private readonly FakeIPhonePhotoSource _phone = new();
    private TestDatabase _db = null!;
    private DestinationIndexService _index = null!;
    private ExactDuplicateDetector _detector = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _index = new DestinationIndexService(
            new DestinationScanner(NullLogger<DestinationScanner>.Instance),
            _db.Repository,
            TimeProvider.System,
            NullLogger<DestinationIndexService>.Instance);
        var lookup = new DestinationContentLookup(_db.Repository, new Sha256HashService(), NullLogger<DestinationContentLookup>.Instance);
        _detector = new ExactDuplicateDetector(_phone, lookup, new TransferRepository(_db.Database), new Sha256HashService(), NullLogger<ExactDuplicateDetector>.Instance);
        await _phone.ConnectAsync(FakePhoneDeviceService.TestPhone, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _destination.Dispose();
    }

    [Fact]
    public async Task Identical_bytes_are_an_exact_duplicate()
    {
        var content = Bytes(5_000);
        var existing = WriteDestination(@"2025\Vacation\IMG_1234.HEIC", content);
        _phone.AddFile("Internal Storage/202409__/IMG_1234.HEIC", content);

        var item = Assert.Single((await ClassifyAsync()).Items);

        Assert.Equal(AssetStatus.ExactDuplicate, item.Status);
        Assert.Equal(existing, item.ExistingPath);
    }

    [Fact]
    public async Task Identical_bytes_with_a_different_name_and_folder_are_still_a_duplicate()
    {
        var content = Bytes(5_000);
        WriteDestination(@"Old Phone\Vacation_2025.heic", content);
        _phone.AddFile("Internal Storage/202409__/IMG_0001.HEIC", content);

        Assert.Equal(AssetStatus.ExactDuplicate, Assert.Single((await ClassifyAsync()).Items).Status);
    }

    [Fact]
    public async Task Same_name_and_size_but_different_bytes_is_new()
    {
        var content = Bytes(5_000);
        var different = (byte[])content.Clone();
        different[2_500] ^= 0xFF;
        WriteDestination("IMG_1234.HEIC", different);
        _phone.AddFile("Internal Storage/202409__/IMG_1234.HEIC", content);

        var plan = await ClassifyAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
        Assert.Equal(1, _phone.OpenCount);
    }

    [Fact]
    public async Task Different_size_is_new_without_reading_the_phone()
    {
        WriteDestination("IMG_1234.HEIC", Bytes(5_000));
        _phone.AddFile("Internal Storage/202409__/IMG_1234.HEIC", Bytes(5_001));

        var plan = await ClassifyAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
        Assert.Equal(0, _phone.OpenCount);
    }

    [Fact]
    public async Task Destination_hash_is_stored_and_reused()
    {
        var content = Bytes(5_000);
        var existing = WriteDestination("a.jpg", content);
        _phone.AddFile("Internal Storage/x/IMG_1.JPG", content);

        await ClassifyAsync();
        var stored = await _db.Repository.GetByPathAsync(existing, Ct);

        Assert.Equal(SHA256.HashData(content), stored!.Sha256);
    }

    [Fact]
    public async Task Live_photo_with_only_the_image_already_present_transfers_just_the_video()
    {
        var image = Bytes(4_000);
        var video = Bytes(9_000);
        WriteDestination("IMG_0010.HEIC", image);
        _phone.AddFile("Internal Storage/a/IMG_0010.HEIC", image);
        _phone.AddFile("Internal Storage/a/IMG_0010.MOV", video);

        var item = Assert.Single((await ClassifyAsync()).Items);

        Assert.True(item.Item.IsLivePhoto);
        Assert.Equal(AssetStatus.New, item.Status);
        Assert.Equal(AssetStatus.ExactDuplicate, item.Components[0].Status);
        Assert.Equal(AssetStatus.New, item.Components[1].Status);
        Assert.Equal(9_000, item.TransferBytes);
    }

    [Fact]
    public async Task Live_photo_is_a_duplicate_only_when_both_parts_exist()
    {
        var image = Bytes(4_000);
        var video = Bytes(9_000);
        WriteDestination("IMG_0010.HEIC", image);
        WriteDestination("IMG_0010.MOV", video);
        _phone.AddFile("Internal Storage/a/IMG_0010.HEIC", image);
        _phone.AddFile("Internal Storage/a/IMG_0010.MOV", video);

        var plan = await ClassifyAsync();

        Assert.Equal(AssetStatus.ExactDuplicate, Assert.Single(plan.Items).Status);
        Assert.Equal(0, plan.TransferBytes);
    }

    [Fact]
    public async Task Unreadable_phone_file_is_treated_as_new_and_counted_unverified()
    {
        var content = Bytes(5_000);
        WriteDestination("a.jpg", content);
        var asset = _phone.AddFile("Internal Storage/x/IMG_1.JPG", content);
        _phone.FailReadsOf(asset, afterBytes: 100, ErrorKind.DeviceIo);

        var plan = await ClassifyAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
        Assert.Equal(1, plan.UnverifiedFiles);
    }

    [Fact]
    public async Task Disconnected_phone_stops_the_check()
    {
        var content = Bytes(5_000);
        WriteDestination("a.jpg", content);
        var asset = _phone.AddFile("Internal Storage/x/IMG_1.JPG", content);
        _phone.FailReadsOf(asset, afterBytes: 100, ErrorKind.DeviceDisconnected);
        await SyncDestinationAsync();

        var result = await _detector.ClassifyAsync(Items(), _destination.Path, null, Ct);

        Assert.Equal(ErrorKind.DeviceDisconnected, result.Error!.Kind);
    }

    [Fact]
    public async Task Destination_file_modified_after_indexing_is_not_trusted_as_a_match()
    {
        var content = Bytes(5_000);
        var existing = WriteDestination("a.jpg", content);
        _phone.AddFile("Internal Storage/x/IMG_1.JPG", content);
        await SyncDestinationAsync();
        File.SetLastWriteTimeUtc(existing, DateTime.UtcNow.AddMinutes(5));

        var result = await _detector.ClassifyAsync(Items(), _destination.Path, null, Ct);

        Assert.Equal(AssetStatus.New, Assert.Single(result.Value.Items).Status);
    }

    [Fact]
    public async Task Indexed_files_outside_the_destination_are_ignored()
    {
        var content = Bytes(5_000);
        using var elsewhere = new TemporaryDirectory();
        File.WriteAllBytes(elsewhere.Combine("a.jpg"), content);
        await _index.SyncAsync(elsewhere.Path, null, Ct);
        _phone.AddFile("Internal Storage/x/IMG_1.JPG", content);

        var plan = await ClassifyAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
        Assert.Equal(0, _phone.OpenCount);
    }

    [Fact]
    public async Task Unknown_phone_size_reads_the_file_and_finds_the_duplicate()
    {
        var content = Bytes(5_000);
        WriteDestination("a.jpg", content);
        _phone.AddFile("Internal Storage/x/IMG_1.JPG", content, reportsSize: false);

        var plan = await ClassifyAsync();

        Assert.Equal(AssetStatus.ExactDuplicate, Assert.Single(plan.Items).Status);
        Assert.Equal(1, _phone.OpenCount);
    }

    [Fact]
    public async Task Wrongly_reported_size_errs_towards_new_never_towards_skipping()
    {
        // The size pre-filter trusts the reported size; if the phone misreports it, the file is
        // copied (the transfer engine re-checks by hash), but it is never wrongly skipped.
        var content = Bytes(5_000);
        WriteDestination("a.jpg", content);
        _phone.AddFile("Internal Storage/x/IMG_1.JPG", content, reportedSize: 4_321);

        var plan = await ClassifyAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
    }

    [Fact]
    public async Task Moved_file_replaces_its_stale_record()
    {
        var content = Bytes(5_000);
        var original = WriteDestination(@"old\a.jpg", content);
        await SyncDestinationAsync();
        var record = await _db.Repository.GetByPathAsync(original, Ct);
        await _db.Repository.SetSha256Async(record!.Id, SHA256.HashData(content), Ct);
        var moved = _destination.Combine(@"new\a.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        File.Move(original, moved);
        _phone.AddFile("Internal Storage/x/IMG_1.JPG", content);

        var plan = await ClassifyAsync();

        Assert.Equal(moved, Assert.Single(plan.Items).ExistingPath);
        Assert.Null(await _db.Repository.GetByPathAsync(original, Ct));
    }

    [Fact]
    public async Task Counts_new_duplicates_and_bytes_to_transfer()
    {
        var shared = Bytes(1_000);
        WriteDestination("dup.jpg", shared);
        _phone.AddFile("Internal Storage/x/IMG_1.JPG", shared);
        _phone.AddFile("Internal Storage/x/IMG_2.JPG", Bytes(2_000));
        _phone.AddFile("Internal Storage/x/IMG_3.MOV", Bytes(3_000));

        var plan = await ClassifyAsync();

        Assert.Equal(3, plan.TotalCount);
        Assert.Equal(2, plan.NewCount);
        Assert.Equal(1, plan.ExactDuplicateCount);
        Assert.Equal(1, plan.VideoCount);
        Assert.Equal(5_000, plan.TransferBytes);
    }

    [Fact]
    public async Task Can_be_cancelled()
    {
        _phone.AddFile("Internal Storage/x/IMG_1.JPG", Bytes(10));
        await SyncDestinationAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await _detector.ClassifyAsync(Items(), _destination.Path, null, cancellation.Token);

        Assert.Equal(ErrorKind.Cancelled, result.Error!.Kind);
    }

    private async Task<ImportPlan> ClassifyAsync()
    {
        await SyncDestinationAsync();
        var result = await _detector.ClassifyAsync(Items(), _destination.Path, null, Ct);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }

    private async Task SyncDestinationAsync()
    {
        var sync = await _index.SyncAsync(_destination.Path, null, Ct);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());
    }

    private IReadOnlyList<MediaItem> Items()
    {
        var assets = _phone.EnumerateAssetsAsync(null, Ct).GetAwaiter().GetResult();
        return LivePhotoGrouper.Group(assets.Value);
    }

    private string WriteDestination(string relativePath, byte[] content)
    {
        var path = _destination.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
