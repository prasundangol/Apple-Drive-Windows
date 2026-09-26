using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Domain.Results;
using AppleDrive.Infrastructure.Database;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Hashing;
using AppleDrive.Infrastructure.Metadata;
using AppleDrive.IntegrationTests.Database;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Transfer;

public sealed class MediaTransferServiceTests : IAsyncLifetime
{
    private readonly TemporaryDirectory _destination = new();
    private readonly FakeIPhonePhotoSource _phone = new();
    private TestDatabase _db = null!;
    private TransferRepository _history = null!;
    private DestinationIndexService _index = null!;
    private ExactDuplicateDetector _detector = null!;
    private DestinationContentLookup _lookup = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _history = new TransferRepository(_db.Database);
        _index = new DestinationIndexService(
            new DestinationScanner(NullLogger<DestinationScanner>.Instance), _db.Repository, TimeProvider.System, NullLogger<DestinationIndexService>.Instance);
        _lookup = new DestinationContentLookup(_db.Repository, new Sha256HashService(), NullLogger<DestinationContentLookup>.Instance);
        _detector = new ExactDuplicateDetector(_phone, _lookup, new Sha256HashService(), NullLogger<ExactDuplicateDetector>.Instance);
        await _phone.ConnectAsync(FakePhoneDeviceService.TestPhone, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _destination.Dispose();
    }

    // ------------------------------------------------------------------ copy and verify

    [Fact]
    public async Task Copies_new_files_verified_indexed_and_recorded()
    {
        var content = Bytes(300_000);
        _phone.AddFile("Internal Storage/DCIM/100APPLE/IMG_0001.JPG", content);

        var result = await TransferAsync(await PlanAsync());

        Assert.Equal(TransferSessionStatus.Completed, result.Status);
        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(content.Length, result.BytesTransferred);
        var target = _destination.Combine("IMG_0001.JPG");
        Assert.Equal(content, File.ReadAllBytes(target));
        Assert.Equal(target, Assert.Single(Assert.Single(result.Items).Components).DestinationPath);

        var indexed = await _db.Repository.GetByPathAsync(target, Ct);
        Assert.Equal(SHA256.HashData(content), indexed!.Sha256);
        Assert.True(indexed.IsAvailable);

        var record = Assert.Single(await _history.GetBySessionAsync(result.SessionId, Ct));
        Assert.Equal(TransferStatus.Completed, record.Status);
        Assert.Equal(target, record.DestinationPath);
        Assert.Equal(content.Length, record.FileSize);
        var session = await _history.GetSessionAsync(result.SessionId, Ct);
        Assert.Equal(TransferSessionStatus.Completed, session!.Status);
        Assert.Equal(1, session.TransferredCount);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Reads_one_file_at_a_time_and_each_only_once()
    {
        for (var i = 0; i < 6; i++)
        {
            _phone.AddFile($"Internal Storage/DCIM/100APPLE/IMG_{i:D4}.JPG", Bytes(200_000 + i));
        }

        var plan = await PlanAsync();
        var readsBefore = _phone.OpenCount;
        var result = await TransferAsync(plan);

        Assert.Equal(6, result.TransferredCount);
        Assert.Equal(1, _phone.MaxConcurrentStreams);
        Assert.Equal(6, _phone.OpenCount - readsBefore);
    }

    [Fact]
    public async Task Exact_duplicates_are_skipped_without_reading_them_again()
    {
        var shared = Bytes(5_000);
        WriteDestination("existing.jpg", shared);
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", shared);
        _phone.AddFile("Internal Storage/a/IMG_2.JPG", Bytes(6_000));
        var plan = await PlanAsync();
        var readsBefore = _phone.OpenCount;

        var result = await TransferAsync(plan);

        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(1, _phone.OpenCount - readsBefore);
        Assert.False(File.Exists(_destination.Combine("IMG_1.JPG")));
    }

    [Fact]
    public async Task Duplicates_are_copied_when_skipping_is_turned_off()
    {
        var shared = Bytes(5_000);
        WriteDestination("existing.jpg", shared);
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", shared);

        var result = await TransferAsync(await PlanAsync(), skipExactDuplicates: false);

        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(shared, File.ReadAllBytes(_destination.Combine("IMG_1.JPG")));
    }

    [Fact]
    public async Task A_copy_that_does_not_match_on_disk_is_discarded()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", Bytes(5_000));
        var plan = await PlanAsync();

        var result = await TransferAsync(plan, hashService: new CorruptingHashService());

        var item = Assert.Single(result.Items);
        Assert.Equal(ItemTransferStatus.Failed, item.Status);
        Assert.Equal(ErrorKind.VerificationFailed, item.Error!.Kind);
        Assert.Empty(MediaFilesInDestination());
        Assert.Equal(TransferStatus.Failed, Assert.Single(await _history.GetBySessionAsync(result.SessionId, Ct)).Status);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Transferred_files_are_exact_duplicates_on_the_next_check()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.HEIC", Bytes(8_000));
        _phone.AddFile("Internal Storage/a/IMG_2.MOV", Bytes(9_000));
        await TransferAsync(await PlanAsync());

        var again = await PlanAsync();

        Assert.Equal(0, again.NewCount);
        Assert.Equal(2, again.ExactDuplicateCount);
    }

    // ------------------------------------------------------------------ naming

    [Fact]
    public async Task Name_clashes_get_the_next_free_number_and_existing_files_are_untouched()
    {
        var first = WriteDestination("IMG.JPG", Bytes(1_000));
        var second = WriteDestination("IMG (1).JPG", Bytes(1_001));
        var firstHash = SHA256.HashData(File.ReadAllBytes(first));
        var secondHash = SHA256.HashData(File.ReadAllBytes(second));
        var content = Bytes(1_002);
        _phone.AddFile("Internal Storage/a/IMG.JPG", content);

        await TransferAsync(await PlanAsync());

        Assert.Equal(content, File.ReadAllBytes(_destination.Combine("IMG (2).JPG")));
        Assert.Equal(firstHash, SHA256.HashData(File.ReadAllBytes(first)));
        Assert.Equal(secondHash, SHA256.HashData(File.ReadAllBytes(second)));
    }

    [Fact]
    public async Task Same_name_in_different_phone_folders_gets_distinct_names()
    {
        var a = Bytes(1_000);
        var b = Bytes(1_001);
        _phone.AddFile("Internal Storage/DCIM/100APPLE/IMG_0001.JPG", a);
        _phone.AddFile("Internal Storage/DCIM/101APPLE/IMG_0001.JPG", b);

        await TransferAsync(await PlanAsync());

        Assert.Equal(a, File.ReadAllBytes(_destination.Combine("IMG_0001.JPG")));
        Assert.Equal(b, File.ReadAllBytes(_destination.Combine("IMG_0001 (1).JPG")));
    }

    // ------------------------------------------------------------------ organization

    [Fact]
    public async Task Year_month_folders_use_the_exif_capture_date()
    {
        var photo = MediaFixtures.Jpeg(MediaFixtures.Exif("2024:03:15 09:30:00", "+05:45"));
        _phone.AddFile("Internal Storage/a/IMG_0001.JPG", photo, createdAt: new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

        await TransferAsync(await PlanAsync(), FolderOrganization.YearMonth);

        var target = _destination.Combine("2024", "03 March", "IMG_0001.JPG");
        Assert.True(File.Exists(target));
        Assert.Equal(new DateTimeOffset(2024, 3, 15, 9, 30, 0, TimeSpan.FromMinutes(345)).UtcDateTime, File.GetLastWriteTimeUtc(target));
        var indexed = await _db.Repository.GetByPathAsync(target, Ct);
        Assert.Equal(new DateTimeOffset(2024, 3, 15, 9, 30, 0, TimeSpan.FromMinutes(345)), indexed!.CaptureDate);
    }

    [Fact]
    public async Task Day_folders_use_the_wall_clock_date_where_the_photo_was_taken()
    {
        // 23:30 on 31 March at UTC-8 is already 1 April in UTC; the photo belongs to 31 March.
        var photo = MediaFixtures.Heic(MediaFixtures.Exif("2024:03:31 23:30:00", "-08:00"));
        _phone.AddFile("Internal Storage/a/IMG_0002.HEIC", photo);

        await TransferAsync(await PlanAsync(), FolderOrganization.YearMonthDay);

        Assert.True(File.Exists(_destination.Combine("2024", "03 March", "31", "IMG_0002.HEIC")));
    }

    [Fact]
    public async Task Videos_are_organized_by_their_quicktime_creation_date()
    {
        var video = MediaFixtures.QuickTime(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), "2024-12-31T22:00:00-0500");
        _phone.AddFile("Internal Storage/a/IMG_0003.MOV", video);

        await TransferAsync(await PlanAsync(), FolderOrganization.YearMonth);

        Assert.True(File.Exists(_destination.Combine("2024", "12 December", "IMG_0003.MOV")));
    }

    [Fact]
    public async Task Without_metadata_the_phone_date_is_used()
    {
        _phone.AddFile("Internal Storage/a/IMG_0004.JPG", Bytes(3_000), createdAt: new DateTimeOffset(2023, 7, 4, 12, 0, 0, TimeSpan.Zero));

        await TransferAsync(await PlanAsync(), FolderOrganization.YearMonth);

        Assert.True(File.Exists(_destination.Combine("2023", "07 July", "IMG_0004.JPG")));
    }

    [Theory]
    [InlineData(FolderOrganization.YearMonth)]
    [InlineData(FolderOrganization.YearMonthDay)]
    public async Task Undated_files_use_the_month_of_their_camera_roll_folder(FolderOrganization organization)
    {
        // Screenshots often have neither EXIF nor a phone-reported date.
        _phone.AddFile("Internal Storage/202504__/IMG_0008.PNG", Bytes(3_000));

        await TransferAsync(await PlanAsync(), organization);

        Assert.True(File.Exists(_destination.Combine("2025", "04 April", "IMG_0008.PNG")));
    }

    // ------------------------------------------------------------------ Live Photos

    [Fact]
    public async Task Live_photo_parts_stay_together_using_the_image_date()
    {
        var image = MediaFixtures.Heic(MediaFixtures.Exif("2024:05:06 07:08:09", "+00:00"));
        var video = MediaFixtures.QuickTime(null);
        _phone.AddFile("Internal Storage/a/IMG_0010.HEIC", image);
        _phone.AddFile("Internal Storage/a/IMG_0010.MOV", video);

        var result = await TransferAsync(await PlanAsync(), FolderOrganization.YearMonth);

        Assert.Equal(1, result.TransferredCount);
        Assert.True(File.Exists(_destination.Combine("2024", "05 May", "IMG_0010.HEIC")));
        Assert.True(File.Exists(_destination.Combine("2024", "05 May", "IMG_0010.MOV")));
    }

    [Fact]
    public async Task Live_photo_parts_share_one_conflict_number()
    {
        WriteDestination("IMG_0010.HEIC", Bytes(1_234));
        _phone.AddFile("Internal Storage/a/IMG_0010.HEIC", Bytes(4_000));
        _phone.AddFile("Internal Storage/a/IMG_0010.MOV", Bytes(9_000));

        await TransferAsync(await PlanAsync());

        Assert.True(File.Exists(_destination.Combine("IMG_0010 (1).HEIC")));
        Assert.True(File.Exists(_destination.Combine("IMG_0010 (1).MOV")));
        Assert.False(File.Exists(_destination.Combine("IMG_0010.MOV")));
    }

    [Fact]
    public async Task Missing_live_photo_video_is_placed_next_to_its_existing_image()
    {
        var image = Bytes(4_000);
        WriteDestination(@"Holidays\Beach.HEIC", image);
        _phone.AddFile("Internal Storage/a/IMG_0010.HEIC", image);
        _phone.AddFile("Internal Storage/a/IMG_0010.MOV", Bytes(9_000));

        await TransferAsync(await PlanAsync(), FolderOrganization.YearMonth);

        Assert.True(File.Exists(_destination.Combine("Holidays", "Beach.MOV")));
        Assert.Equal(2, MediaFilesInDestination().Count);
    }

    // ------------------------------------------------------------------ what the phone delivers

    [Fact]
    public async Task Wrong_content_from_the_phone_is_caught_and_read_again()
    {
        var content = Bytes(5_000);
        WriteDestination("same-size.jpg", Bytes(5_000));
        var asset = _phone.AddFile("Internal Storage/a/IMG_1.JPG", content);
        var plan = await PlanAsync(); // Same size as a destination file, so the plan holds its hash.
        Assert.NotNull(plan.Items[0].Components[0].Sha256);
        _phone.DeliverOnce(asset, Bytes(5_000));
        var connectsBefore = _phone.ConnectCount;

        var result = await TransferAsync(plan);

        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(content, File.ReadAllBytes(_destination.Combine("IMG_1.JPG")));
        Assert.Equal(connectsBefore + 1, _phone.ConnectCount);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Delivered_size_different_from_reported_is_read_again_after_reconnecting()
    {
        var content = Bytes(5_000);
        var asset = _phone.AddFile("Internal Storage/a/IMG_1.JPG", content);
        _phone.DeliverOnce(asset, Bytes(4_000));
        var connectsBefore = _phone.ConnectCount;

        var result = await TransferAsync(await PlanAsync());

        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(content, File.ReadAllBytes(_destination.Combine("IMG_1.JPG")));
        Assert.Equal(connectsBefore + 1, _phone.ConnectCount);
    }

    [Fact]
    public async Task Persistently_misreported_size_fails_verification()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", Bytes(5_000), reportedSize: 7_000);
        var plan = await PlanAsync();
        var readsBefore = _phone.OpenCount;

        var result = await TransferAsync(plan);

        var item = Assert.Single(result.Items);
        Assert.Equal(ItemTransferStatus.Failed, item.Status);
        Assert.Equal(ErrorKind.VerificationFailed, item.Error!.Kind);
        Assert.Equal(2, _phone.OpenCount - readsBefore);
        Assert.Empty(MediaFilesInDestination());
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Heic_converted_to_jpeg_by_the_phone_is_kept_as_jpg()
    {
        var jpeg = MediaFixtures.Jpeg(null, payload: 5_000);
        _phone.AddFile("Internal Storage/a/IMG_0005.HEIC", jpeg, reportedSize: jpeg.Length + 3_000);

        var result = await TransferAsync(await PlanAsync());

        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(jpeg, File.ReadAllBytes(_destination.Combine("IMG_0005.JPG")));
        Assert.False(File.Exists(_destination.Combine("IMG_0005.HEIC")));
    }

    [Fact]
    public async Task Content_found_in_the_destination_after_copying_is_discarded()
    {
        // The phone converts to JPEG on the fly, so the reported size matched nothing during the
        // check, but the delivered JPEG is already in the destination.
        var jpeg = MediaFixtures.Jpeg(null, payload: 5_000);
        var existing = WriteDestination("Imported earlier.JPG", jpeg);
        _phone.AddFile("Internal Storage/a/IMG_0005.HEIC", jpeg, reportedSize: jpeg.Length + 3_000);

        var result = await TransferAsync(await PlanAsync());

        var item = Assert.Single(result.Items);
        Assert.Equal(ItemTransferStatus.DuplicateFound, item.Status);
        Assert.Equal(existing, item.Components[0].DestinationPath);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(MediaFilesInDestination());
        Assert.Equal(TransferStatus.Duplicate, Assert.Single(await _history.GetBySessionAsync(result.SessionId, Ct)).Status);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task The_same_photo_twice_on_the_phone_is_copied_once()
    {
        var content = Bytes(5_000);
        _phone.AddFile("Internal Storage/DCIM/100APPLE/IMG_1.JPG", content);
        _phone.AddFile("Internal Storage/DCIM/101APPLE/IMG_9.JPG", content);

        var result = await TransferAsync(await PlanAsync());

        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(MediaFilesInDestination());
    }

    // ------------------------------------------------------------------ failures

    [Fact]
    public async Task A_read_error_is_retried_once_then_the_file_fails_and_the_rest_continue()
    {
        var bad = _phone.AddFile("Internal Storage/a/IMG_1.JPG", Bytes(300_000));
        _phone.AddFile("Internal Storage/a/IMG_2.JPG", Bytes(300_001));
        _phone.FailReadsOf(bad, afterBytes: 100_000, ErrorKind.DeviceIo);
        var plan = await PlanAsync();
        var connectsBefore = _phone.ConnectCount;

        var result = await TransferAsync(plan);

        Assert.Equal(TransferSessionStatus.Completed, result.Status);
        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(ErrorKind.DeviceIo, Assert.Single(result.FailedItems).Error!.Kind);
        Assert.Equal(connectsBefore + 1, _phone.ConnectCount);
        Assert.True(File.Exists(_destination.Combine("IMG_2.JPG")));
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task A_read_error_that_clears_after_reconnecting_succeeds()
    {
        var content = Bytes(300_000);
        var flaky = _phone.AddFile("Internal Storage/a/IMG_1.JPG", content);
        _phone.FailReadsOf(flaky, afterBytes: 100_000, ErrorKind.DeviceIo);
        _phone.OnOpen = asset => _phone.HealReadsOf(asset); // The failure applies to the current read only.

        var result = await TransferAsync(await PlanAsync());

        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(content, File.ReadAllBytes(_destination.Combine("IMG_1.JPG")));
    }

    [Fact]
    public async Task Unplugged_phone_stops_the_transfer_and_keeps_what_was_copied()
    {
        var first = Bytes(200_000);
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", first);
        var second = _phone.AddFile("Internal Storage/a/IMG_2.JPG", Bytes(200_001));
        _phone.AddFile("Internal Storage/a/IMG_3.JPG", Bytes(200_002));
        _phone.FailReadsOf(second, afterBytes: 50_000, ErrorKind.DeviceDisconnected);

        var result = await TransferAsync(await PlanAsync());

        Assert.Equal(TransferSessionStatus.Stopped, result.Status);
        Assert.Equal(ErrorKind.DeviceDisconnected, result.StopReason!.Kind);
        Assert.Equal(1, result.TransferredCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(1, result.NotAttemptedCount);
        Assert.Equal(first, File.ReadAllBytes(_destination.Combine("IMG_1.JPG")));
        Assert.Single(MediaFilesInDestination());
        Assert.DoesNotContain(await _history.GetBySessionAsync(result.SessionId, Ct), record => record.Status == TransferStatus.InProgress);
        Assert.Equal(TransferSessionStatus.Stopped, (await _history.GetSessionAsync(result.SessionId, Ct))!.Status);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Missing_destination_is_reported_before_anything_is_read()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", Bytes(1_000));
        var plan = await PlanAsync();
        var readsBefore = _phone.OpenCount;
        Directory.Delete(_destination.Path, recursive: true);

        var result = await Service().TransferAsync(Request(plan), null, Ct);

        Assert.Equal(ErrorKind.DestinationUnavailable, result.Error!.Kind);
        Assert.Equal(readsBefore, _phone.OpenCount);
    }

    [Fact]
    public async Task No_phone_is_reported_as_an_error()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", Bytes(1_000));
        var plan = await PlanAsync();
        await _phone.DisconnectAsync();

        var result = await Service().TransferAsync(Request(plan), null, Ct);

        Assert.Equal(ErrorKind.DeviceNotFound, result.Error!.Kind);
    }

    [Fact]
    public async Task Destination_without_write_permission_stops_the_transfer()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", Bytes(1_000));
        _phone.AddFile("Internal Storage/a/IMG_2.JPG", Bytes(1_001));
        var plan = await PlanAsync();
        var directory = new DirectoryInfo(_destination.Path);
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.AppendData,
            AccessControlType.Deny);
        var security = directory.GetAccessControl();
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        try
        {
            var result = await TransferAsync(plan);

            Assert.Equal(TransferSessionStatus.Stopped, result.Status);
            Assert.Equal(ErrorKind.AccessDenied, result.StopReason!.Kind);
            Assert.Equal(0, result.TransferredCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Equal(1, result.NotAttemptedCount);
        }
        finally
        {
            security.RemoveAccessRule(rule);
            directory.SetAccessControl(security);
        }

        Assert.Empty(MediaFilesInDestination());
    }

    // ------------------------------------------------------------------ cancel and retry

    [Fact]
    public async Task Cancelling_leaves_no_partial_files_and_a_consistent_history()
    {
        for (var i = 0; i < 5; i++)
        {
            _phone.AddFile($"Internal Storage/a/IMG_{i}.JPG", Bytes(200_000 + i));
        }

        var plan = await PlanAsync();
        using var cancellation = new CancellationTokenSource();
        var opens = 0;
        _phone.OnOpen = _ =>
        {
            if (++opens == 3)
            {
                cancellation.Cancel();
            }
        };

        var result = await Service().TransferAsync(Request(plan), null, cancellation.Token);

        var run = result.Value;
        Assert.Equal(TransferSessionStatus.Cancelled, run.Status);
        Assert.True(run.NotAttemptedCount >= 3, $"{run.NotAttemptedCount} not attempted");
        Assert.Equal(5, run.TransferredCount + run.NotAttemptedCount);
        Assert.Equal(run.TransferredCount, MediaFilesInDestination().Count);
        foreach (var file in MediaFilesInDestination())
        {
            Assert.NotNull((await _db.Repository.GetByPathAsync(file, Ct))?.Sha256);
        }

        Assert.DoesNotContain(await _history.GetBySessionAsync(run.SessionId, Ct), record => record.Status == TransferStatus.InProgress);
        Assert.Equal(TransferSessionStatus.Cancelled, (await _history.GetSessionAsync(run.SessionId, Ct))!.Status);
        AssertNoPartialFiles();
    }

    [Fact]
    public async Task Retry_copies_only_what_failed()
    {
        var bad = _phone.AddFile("Internal Storage/a/IMG_1.JPG", Bytes(300_000));
        _phone.AddFile("Internal Storage/a/IMG_2.JPG", Bytes(300_001));
        _phone.FailReadsOf(bad, afterBytes: 100_000, ErrorKind.DeviceIo);
        var first = await TransferAsync(await PlanAsync());
        _phone.HealReadsOf(bad);
        var readsBefore = _phone.OpenCount;

        var retryItems = first.GetRetryItems();
        var retry = await Service().TransferAsync(Request(retryItems), null, Ct);

        Assert.Single(retryItems);
        Assert.Equal(1, retry.Value.TransferredCount);
        Assert.Equal(1, _phone.OpenCount - readsBefore);
        Assert.Equal(2, MediaFilesInDestination().Count);
    }

    [Fact]
    public async Task Retrying_a_live_photo_copies_the_missing_video_next_to_its_image()
    {
        var image = MediaFixtures.Heic(MediaFixtures.Exif("2024:05:06 07:08:09", "+00:00"));
        _phone.AddFile("Internal Storage/a/IMG_0010.HEIC", image);
        var video = _phone.AddFile("Internal Storage/a/IMG_0010.MOV", Bytes(300_000));
        _phone.FailReadsOf(video, afterBytes: 100_000, ErrorKind.DeviceIo);
        var first = await TransferAsync(await PlanAsync(), FolderOrganization.YearMonth);
        Assert.Equal(1, first.FailedCount);
        Assert.True(File.Exists(_destination.Combine("2024", "05 May", "IMG_0010.HEIC")));
        _phone.HealReadsOf(video);

        var retry = await Service().TransferAsync(Request(first.GetRetryItems(), FolderOrganization.YearMonth), null, Ct);

        Assert.Equal(1, retry.Value.TransferredCount);
        Assert.True(File.Exists(_destination.Combine("2024", "05 May", "IMG_0010.MOV")));
        Assert.Equal(2, MediaFilesInDestination().Count);
    }

    // ------------------------------------------------------------------ progress

    [Fact]
    public async Task Progress_ends_with_every_item_counted()
    {
        _phone.AddFile("Internal Storage/a/IMG_1.JPG", Bytes(100_000));
        _phone.AddFile("Internal Storage/a/IMG_2.JPG", Bytes(150_000));
        var shared = Bytes(1_000);
        WriteDestination("dup.jpg", shared);
        _phone.AddFile("Internal Storage/a/IMG_3.JPG", shared);
        var reports = new List<TransferProgress>();
        var progress = new SynchronousProgress<TransferProgress>(report =>
        {
            lock (reports)
            {
                reports.Add(report);
            }
        });

        await Service().TransferAsync(Request(await PlanAsync()), progress, Ct);

        var last = reports[^1];
        Assert.Equal(2, last.ItemsTotal);
        Assert.Equal(2, last.ItemsDone);
        Assert.Equal(2, last.Transferred);
        Assert.Equal(1, last.Skipped);
        Assert.Equal(250_000, last.BytesTotal);
        Assert.Equal(250_000, last.BytesDone);
        Assert.Contains(reports, report => report.CurrentFile == "IMG_2.JPG");
    }

    // ------------------------------------------------------------------ helpers

    private MediaTransferService Service(IHashService? hashService = null) => new(
        _phone,
        hashService ?? new Sha256HashService(),
        new CaptureDateReader(),
        _lookup,
        _db.Repository,
        _history,
        new DestinationNameReservations(),
        TimeProvider.System,
        NullLogger<MediaTransferService>.Instance);

    private TransferRequest Request(ImportPlan plan, FolderOrganization organization = FolderOrganization.Flat, bool skipExactDuplicates = true) =>
        new(plan.Items, plan.DestinationRoot, organization, skipExactDuplicates);

    private TransferRequest Request(IReadOnlyList<ItemClassification> items, FolderOrganization organization = FolderOrganization.Flat) =>
        new(items, _destination.Path, organization);

    private async Task<TransferRunResult> TransferAsync(
        ImportPlan plan,
        FolderOrganization organization = FolderOrganization.Flat,
        bool skipExactDuplicates = true,
        IHashService? hashService = null)
    {
        var result = await Service(hashService).TransferAsync(Request(plan, organization, skipExactDuplicates), null, Ct);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Value;
    }

    private async Task<ImportPlan> PlanAsync()
    {
        var sync = await _index.SyncAsync(_destination.Path, null, Ct);
        Assert.True(sync.IsSuccess, sync.Error?.ToString());
        var assets = await _phone.EnumerateAssetsAsync(null, Ct);
        var plan = await _detector.ClassifyAsync(LivePhotoGrouper.Group(assets.Value), _destination.Path, null, Ct);
        Assert.True(plan.IsSuccess, plan.Error?.ToString());
        return plan.Value;
    }

    private string WriteDestination(string relativePath, byte[] content)
    {
        var path = _destination.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private List<string> MediaFilesInDestination() =>
        Directory.GetFiles(_destination.Path, "*", SearchOption.AllDirectories).Where(MediaFormats.IsSupported).ToList();

    private void AssertNoPartialFiles() =>
        Assert.Empty(Directory.GetFiles(_destination.Path, "*" + MediaTransferService.PartialFileSuffix, SearchOption.AllDirectories));

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Reports a wrong hash for files still being verified, as if the disk had corrupted them.</summary>
    private sealed class CorruptingHashService : IHashService
    {
        private readonly Sha256HashService _inner = new();

        public Task<HashResult> ComputeAsync(Stream stream, IProgress<long>? bytesRead, CancellationToken cancellationToken) =>
            _inner.ComputeAsync(stream, bytesRead, cancellationToken);

        public async Task<HashResult> ComputeFileAsync(string path, CancellationToken cancellationToken)
        {
            var result = await _inner.ComputeFileAsync(path, cancellationToken);
            if (!path.EndsWith(MediaTransferService.PartialFileSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            var corrupted = (byte[])result.Sha256.Clone();
            corrupted[0] ^= 0xFF;
            return result with { Sha256 = corrupted };
        }
    }
}
