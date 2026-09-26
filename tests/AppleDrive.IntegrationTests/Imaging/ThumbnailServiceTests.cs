using System.Security.Cryptography;
using AppleDrive.Application.Settings;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Imaging;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Imaging;

public sealed class ThumbnailServiceTests : IAsyncLifetime, IDisposable
{
    private readonly TemporaryDirectory _data = new();
    private readonly TemporaryDirectory _files = new();
    private readonly FakeIPhonePhotoSource _phone = new();
    private readonly WicPerceptualHashService _hasher = new(NullLogger<WicPerceptualHashService>.Instance);
    private ThumbnailService _service = null!;

    public async ValueTask InitializeAsync()
    {
        await _phone.ConnectAsync(FakePhoneDeviceService.TestPhone, CancellationToken.None);
        _service = new ThumbnailService(
            _phone,
            new FakeSettingsService(new AppSettings { ThumbnailCacheFolder = _data.Combine("Previews") }),
            new AppPaths(_data.Path),
            NullLogger<ThumbnailService>.Instance);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Dispose()
    {
        _service.Dispose();
        _data.Dispose();
        _files.Dispose();
    }

    [Fact]
    public async Task A_sideways_camera_photo_gets_an_upright_preview_from_the_file()
    {
        // Stored landscape, tagged "rotate 90° clockwise": an upright portrait photo.
        var photo = MediaFixtures.WithExif(TestImages.Render(1, 400, 300), MediaFixtures.Exif("2024:01:01 10:00:00", orientation: 6));
        var asset = _phone.AddFile("Internal Storage/a/IMG_0001.JPG", photo);
        _phone.SetThumbnail(asset, TestImages.Render(1, 160, 120));

        var preview = await _service.GetPhoneThumbnailAsync(asset, Ct);

        var size = await SizeAsync(preview);
        Assert.Equal((192, 256), size);
        Assert.Equal(0, _phone.ThumbnailOpenCount);
        Assert.Equal(1, _phone.OpenCount);
        Assert.StartsWith(_data.Combine("Previews"), preview);
    }

    [Fact]
    public async Task Screenshots_use_the_phones_own_thumbnail()
    {
        var asset = _phone.AddFile("Internal Storage/a/IMG_0002.PNG", TestImages.Render(2, 1200, 2600, TestImageFormat.Png));
        _phone.SetThumbnail(asset, TestImages.Render(2, 74, 160));

        var preview = await _service.GetPhoneThumbnailAsync(asset, Ct);

        Assert.Equal((74, 160), await SizeAsync(preview));
        Assert.Equal(1, _phone.ThumbnailOpenCount);
        Assert.Equal(0, _phone.OpenCount);
    }

    [Fact]
    public async Task Videos_use_the_phones_thumbnail_or_have_none()
    {
        var withThumbnail = _phone.AddFile("Internal Storage/a/IMG_0003.MOV", RandomNumberGenerator.GetBytes(10_000));
        _phone.SetThumbnail(withThumbnail, TestImages.Render(3, 160, 90));
        var withoutThumbnail = _phone.AddFile("Internal Storage/a/IMG_0004.MOV", RandomNumberGenerator.GetBytes(10_000));

        Assert.NotNull(await _service.GetPhoneThumbnailAsync(withThumbnail, Ct));
        Assert.Null(await _service.GetPhoneThumbnailAsync(withoutThumbnail, Ct));
        Assert.Equal(0, _phone.OpenCount); // A video is never read just for a preview.
    }

    [Fact]
    public async Task A_photo_whose_format_cannot_be_decoded_falls_back_to_the_thumbnail()
    {
        var asset = _phone.AddFile("Internal Storage/a/IMG_0005.HEIC", RandomNumberGenerator.GetBytes(20_000));
        _phone.SetThumbnail(asset, TestImages.Render(5, 160, 120));

        Assert.NotNull(await _service.GetPhoneThumbnailAsync(asset, Ct));
        Assert.Equal(1, _phone.OpenCount);
        Assert.Equal(1, _phone.ThumbnailOpenCount);
    }

    [Fact]
    public async Task Previews_are_generated_once_and_then_served_from_the_cache()
    {
        var asset = _phone.AddFile("Internal Storage/a/IMG_0006.JPG", TestImages.Render(6));

        var requests = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _service.GetPhoneThumbnailAsync(asset, Ct)));
        var again = await _service.GetPhoneThumbnailAsync(asset, Ct);

        Assert.All(requests, path => Assert.Equal(again, path));
        Assert.Equal(1, _phone.OpenCount);
        Assert.Equal(1, _phone.MaxConcurrentStreams);
    }

    [Fact]
    public async Task Phone_reads_for_previews_are_one_at_a_time()
    {
        var assets = Enumerable.Range(0, 6).Select(i => _phone.AddFile($"Internal Storage/a/IMG_1{i:D3}.JPG", TestImages.Render(10 + i))).ToList();

        await Task.WhenAll(assets.Select(asset => _service.GetPhoneThumbnailAsync(asset, Ct)));

        Assert.Equal(6, _phone.OpenCount);
        Assert.Equal(1, _phone.MaxConcurrentStreams);
    }

    [Fact]
    public async Task A_cancelled_request_can_be_made_again()
    {
        var asset = _phone.AddFile("Internal Storage/a/IMG_0007.JPG", TestImages.Render(7));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _service.GetPhoneThumbnailAsync(asset, cancelled.Token));

        Assert.NotNull(await _service.GetPhoneThumbnailAsync(asset, Ct));
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_cancel_the_preview_for_another()
    {
        var asset = _phone.AddFile("Internal Storage/a/IMG_0008.JPG", TestImages.Render(8));
        _phone.OnOpen = _ => Thread.Sleep(300); // Slow phone: the first caller gives up meanwhile.
        using var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var first = _service.GetPhoneThumbnailAsync(asset, impatient.Token);
        var second = _service.GetPhoneThumbnailAsync(asset, Ct);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.NotNull(await second);
        Assert.Equal(1, _phone.OpenCount);
    }

    [Fact]
    public async Task Destination_files_get_previews_from_the_shell()
    {
        var path = _files.Combine("Holiday.jpg");
        await File.WriteAllBytesAsync(path, TestImages.Render(8, 1600, 1200), Ct);

        var preview = await _service.GetFileThumbnailAsync(path, Ct);

        var (width, height) = await SizeAsync(preview);
        Assert.InRange(Math.Max(width, height), 1, 256);
        Assert.True(width > height);
    }

    [Fact]
    public async Task A_changed_file_gets_a_new_preview_and_a_missing_one_none()
    {
        var path = _files.Combine("A.jpg");
        await File.WriteAllBytesAsync(path, TestImages.Render(9), Ct);
        var first = await _service.GetFileThumbnailAsync(path, Ct);
        await File.WriteAllBytesAsync(path, TestImages.Render(10, 600, 900), Ct);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var second = await _service.GetFileThumbnailAsync(path, Ct);

        Assert.NotEqual(first, second);
        Assert.Null(await _service.GetFileThumbnailAsync(_files.Combine("missing.jpg"), Ct));
    }

    private async Task<(int Width, int Height)> SizeAsync(string? preview)
    {
        Assert.NotNull(preview);
        Assert.True(File.Exists(preview));
        var fingerprint = await _hasher.ComputeFileAsync(preview, Ct);
        Assert.NotNull(fingerprint);
        return (fingerprint.Width, fingerprint.Height);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
