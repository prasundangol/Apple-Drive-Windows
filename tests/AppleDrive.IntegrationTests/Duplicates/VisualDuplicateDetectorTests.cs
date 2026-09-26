using System.Security.Cryptography;
using AppleDrive.Application.Services;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Infrastructure.Database;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Hashing;
using AppleDrive.Infrastructure.Imaging;
using AppleDrive.Infrastructure.Metadata;
using AppleDrive.IntegrationTests.Database;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Duplicates;

public sealed class VisualDuplicateDetectorTests : IAsyncLifetime
{
    private readonly TemporaryDirectory _destination = new();
    private readonly FakeIPhonePhotoSource _phone = new();
    private TestDatabase _db = null!;
    private TransferRepository _history = null!;
    private DestinationContentLookup _lookup = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _history = new TransferRepository(_db.Database);
        _lookup = new DestinationContentLookup(_db.Repository, new Sha256HashService(), NullLogger<DestinationContentLookup>.Instance);
        await _phone.ConnectAsync(FakePhoneDeviceService.TestPhone, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _destination.Dispose();
    }

    [Fact]
    public async Task A_resized_copy_in_the_destination_makes_a_possible_duplicate()
    {
        var existing = WriteDestination(@"Old phone\Holiday.jpg", TestImages.Render(1, 400, 300, quality: 0.6));
        var photo = _phone.AddFile("Internal Storage/a/IMG_0001.PNG", TestImages.Render(1, 1600, 1200, TestImageFormat.Png));
        _phone.SetThumbnail(photo, TestImages.Render(1, 160, 120, quarterTurns: 1));

        var plan = await PlanAsync();

        var item = Assert.Single(plan.Items);
        Assert.Equal(AssetStatus.PossibleDuplicate, item.Status);
        Assert.Equal(existing, item.SimilarComponent!.SimilarPath);
        Assert.Equal(Similarity.VeryHigh, item.SimilarComponent.Similarity);
        Assert.Equal(1, plan.PossibleDuplicateCount);
        Assert.Equal(0, plan.NewCount);
        Assert.True(plan.HasWork);
        Assert.Equal(1, _phone.ThumbnailOpenCount);
        Assert.Equal(1, _phone.OpenCount); // Full read only to confirm the candidate.
    }

    [Fact]
    public async Task A_different_picture_stays_new_without_reading_the_full_file()
    {
        WriteDestination("Other.jpg", TestImages.Render(2));
        var photo = _phone.AddFile("Internal Storage/a/IMG_0001.PNG", TestImages.Render(3, format: TestImageFormat.Png));
        _phone.SetThumbnail(photo, TestImages.Render(3, 160, 120));

        var plan = await PlanAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
        Assert.Equal(0, _phone.OpenCount);
    }

    [Fact]
    public async Task Jpegs_are_compared_from_the_full_file_not_the_thumbnail()
    {
        WriteDestination("Copy.jpg", TestImages.Render(4, 640, 480, quality: 0.5));
        var photo = _phone.AddFile("Internal Storage/a/IMG_0001.JPG", TestImages.Render(4, 1280, 960));
        _phone.SetThumbnail(photo, TestImages.Render(5, 160, 120)); // A stale thumbnail of another picture.

        var plan = await PlanAsync();

        Assert.Equal(AssetStatus.PossibleDuplicate, Assert.Single(plan.Items).Status);
        Assert.Equal(0, _phone.ThumbnailOpenCount);
    }

    [Fact]
    public async Task A_thumbnail_match_is_rejected_when_the_full_image_differs()
    {
        WriteDestination("Lookalike.jpg", TestImages.Render(6));
        var photo = _phone.AddFile("Internal Storage/a/IMG_0001.PNG", TestImages.Render(7, format: TestImageFormat.Png));
        _phone.SetThumbnail(photo, TestImages.Render(6, 160, 120)); // Thumbnail of a different version.

        var plan = await PlanAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
        Assert.Equal(1, _phone.OpenCount);
    }

    [Fact]
    public async Task No_images_in_the_destination_means_nothing_is_read()
    {
        WriteDestination("clip.mov", RandomNumberGenerator.GetBytes(1_000));
        var photo = _phone.AddFile("Internal Storage/a/IMG_0001.PNG", TestImages.Render(8, format: TestImageFormat.Png));
        _phone.SetThumbnail(photo, TestImages.Render(8, 160, 120));

        var plan = await PlanAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
        Assert.Equal(0, _phone.ThumbnailOpenCount);
        Assert.Equal(0, _phone.OpenCount);
    }

    [Fact]
    public async Task Destination_fingerprints_are_stored_for_next_time()
    {
        var existing = WriteDestination("A.jpg", TestImages.Render(9));
        _phone.AddFile("Internal Storage/a/IMG_0001.JPG", TestImages.Render(10));

        await PlanAsync();

        Assert.NotNull((await _db.Repository.GetByPathAsync(existing, Ct))!.PerceptualHash);
    }

    [Fact]
    public async Task Exact_duplicates_and_videos_are_not_compared_visually()
    {
        var same = TestImages.Render(11);
        WriteDestination("Same.jpg", same);
        _phone.AddFile("Internal Storage/a/IMG_0001.JPG", same);
        _phone.AddFile("Internal Storage/a/IMG_0002.MOV", RandomNumberGenerator.GetBytes(3_000));

        var plan = await PlanAsync();

        Assert.Equal(1, plan.ExactDuplicateCount);
        Assert.Equal(1, plan.NewCount);
        Assert.Equal(1, _phone.OpenCount); // Only the exact-duplicate check read the JPEG.
    }

    [Fact]
    public async Task An_image_that_cannot_be_decoded_is_counted_and_treated_as_new()
    {
        WriteDestination("A.jpg", TestImages.Render(12));
        _phone.AddFile("Internal Storage/a/IMG_0001.PNG", RandomNumberGenerator.GetBytes(4_000));

        var plan = await PlanAsync();

        Assert.Equal(AssetStatus.New, Assert.Single(plan.Items).Status);
        Assert.Equal(1, plan.VisuallyUncheckedImages);
    }

    [Fact]
    public async Task A_live_photo_whose_image_looks_familiar_is_a_possible_duplicate_as_a_whole()
    {
        WriteDestination("Holiday.jpg", TestImages.Render(13, 600, 450));
        var image = _phone.AddFile("Internal Storage/a/IMG_0010.PNG", TestImages.Render(13, 1200, 900, TestImageFormat.Png));
        _phone.AddFile("Internal Storage/a/IMG_0010.MOV", RandomNumberGenerator.GetBytes(5_000));
        _phone.SetThumbnail(image, TestImages.Render(13, 160, 120));
        var assets = await _phone.EnumerateAssetsAsync(null, Ct);
        Assert.Single(LivePhotoGrouper.Group(assets.Value));

        var plan = await PlanAsync();

        var item = Assert.Single(plan.Items);
        Assert.True(item.Item.IsLivePhoto);
        Assert.Equal(AssetStatus.PossibleDuplicate, item.Status);
        Assert.Equal(AssetStatus.New, item.Components[1].Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Possible_duplicates_are_copied_only_if_the_user_includes_them(bool include)
    {
        WriteDestination("Holiday.jpg", TestImages.Render(14, 400, 300));
        var photo = _phone.AddFile("Internal Storage/a/IMG_0001.PNG", TestImages.Render(14, 1200, 900, TestImageFormat.Png));
        _phone.SetThumbnail(photo, TestImages.Render(14, 160, 120));
        _phone.AddFile("Internal Storage/a/IMG_0002.MOV", RandomNumberGenerator.GetBytes(5_000));
        var plan = await PlanAsync();
        var service = new MediaTransferService(
            _phone, new Sha256HashService(), new CaptureDateReader(), _lookup, _db.Repository, _history,
            new DestinationNameReservations(), TimeProvider.System, NullLogger<MediaTransferService>.Instance);

        var result = (await service.TransferAsync(
            new TransferRequest(plan.Items, _destination.Path, FolderOrganization.Flat, IncludePossibleDuplicates: include), null, Ct)).Value;

        Assert.Equal(include, File.Exists(_destination.Combine("IMG_0001.PNG")));
        Assert.True(File.Exists(_destination.Combine("IMG_0002.MOV")));
        Assert.True(File.Exists(_destination.Combine("Holiday.jpg")));
        Assert.Equal(include ? 1 : 0, result.PossibleDuplicatesCopiedCount);
        Assert.Equal(include ? 0 : 1, result.PossibleDuplicatesSkippedCount);
        Assert.Empty(result.GetRetryItems());
    }

    private async Task<ImportPlan> PlanAsync()
    {
        var index = new DestinationIndexService(
            new DestinationScanner(NullLogger<DestinationScanner>.Instance), _db.Repository, TimeProvider.System, NullLogger<DestinationIndexService>.Instance);
        Assert.True((await index.SyncAsync(_destination.Path, null, Ct)).IsSuccess);
        var assets = await _phone.EnumerateAssetsAsync(null, Ct);
        var exact = await new ExactDuplicateDetector(_phone, _lookup, _history, new Sha256HashService(), NullLogger<ExactDuplicateDetector>.Instance)
            .ClassifyAsync(LivePhotoGrouper.Group(assets.Value), _destination.Path, null, Ct);
        Assert.True(exact.IsSuccess, exact.Error?.ToString());
        var visual = await new VisualDuplicateDetector(
                _phone, _db.Repository, new WicPerceptualHashService(NullLogger<WicPerceptualHashService>.Instance), NullLogger<VisualDuplicateDetector>.Instance)
            .CheckAsync(exact.Value, null, Ct);
        Assert.True(visual.IsSuccess, visual.Error?.ToString());
        return visual.Value;
    }

    private string WriteDestination(string relativePath, byte[] content)
    {
        var path = _destination.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
