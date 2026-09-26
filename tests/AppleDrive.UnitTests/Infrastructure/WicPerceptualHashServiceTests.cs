using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Infrastructure.Imaging;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.UnitTests.Infrastructure;

public sealed class WicPerceptualHashServiceTests
{
    private readonly WicPerceptualHashService _hasher = new(NullLogger<WicPerceptualHashService>.Instance);

    [Fact]
    public async Task Same_image_gives_the_same_fingerprint()
    {
        var image = TestImages.Render(1);

        var a = await FingerprintAsync(image);
        var b = await FingerprintAsync(image);

        Assert.Equal(a, b);
        Assert.Equal(800, a.Width);
        Assert.Equal(600, a.Height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Resized_copy_is_a_confirmed_match(int seed)
    {
        await AssertConfirmedAsync(TestImages.Render(seed, 1600, 1200), TestImages.Render(seed, 400, 300));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public async Task Recompressed_copy_is_a_confirmed_match(int seed)
    {
        await AssertConfirmedAsync(TestImages.Render(seed, quality: 0.95), TestImages.Render(seed, quality: 0.3));
    }

    [Fact]
    public async Task Converted_copy_is_a_confirmed_match()
    {
        await AssertConfirmedAsync(TestImages.Render(6, format: TestImageFormat.Png), TestImages.Render(6, 1024, 768, TestImageFormat.Jpeg, quality: 0.7));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public async Task Slightly_modified_copy_is_a_confirmed_match(int seed)
    {
        await AssertConfirmedAsync(TestImages.Render(seed), TestImages.Render(seed, modified: true));
    }

    [Fact]
    public async Task Different_pictures_are_not_matches()
    {
        var fingerprints = new List<ImageFingerprint>();
        for (var seed = 100; seed < 120; seed++)
        {
            fingerprints.Add(await FingerprintAsync(TestImages.Render(seed)));
        }

        for (var i = 0; i < fingerprints.Count; i++)
        {
            for (var j = i + 1; j < fingerprints.Count; j++)
            {
                var candidate = ImageFingerprint.Distance(fingerprints[i].Hash, fingerprints[j].Hash) <= VisualDuplicateDetector.CandidateDistance;
                var confirmed = fingerprints[i].Detail.DistanceTo(fingerprints[j].Detail) <= VisualDuplicateDetector.ConfirmedDistance;
                Assert.False(candidate && confirmed, $"Pictures {100 + i} and {100 + j} matched");
            }
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public async Task A_sideways_or_mirrored_thumbnail_matches_the_upright_photo(int quarterTurns, bool mirrored)
    {
        var upright = await FingerprintAsync(TestImages.Render(9, 1200, 900));
        using var thumbnail = new MemoryStream(TestImages.Render(9, 160, 120, quarterTurns: quarterTurns, mirrored: mirrored));

        var orientations = await _hasher.ComputeAllOrientationsAsync(thumbnail, CancellationToken.None);

        Assert.NotNull(orientations);
        Assert.Equal(8, orientations.Count);
        Assert.InRange(orientations.Min(hash => ImageFingerprint.Distance(hash, upright.Hash)), 0, 4);
    }

    [Fact]
    public async Task Reads_forward_only_streams_like_the_phones()
    {
        var image = TestImages.Render(10);
        using var forwardOnly = new ForwardOnlyStream(image);

        var fingerprint = await _hasher.ComputeAsync(forwardOnly, CancellationToken.None);

        Assert.Equal(await FingerprintAsync(image), fingerprint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(5_000)]
    public async Task Undecodable_data_has_no_fingerprint(int length)
    {
        using var garbage = new MemoryStream(new byte[length]);

        Assert.Null(await _hasher.ComputeAsync(garbage, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_image_does_not_throw()
    {
        var image = TestImages.Render(11);
        using var truncated = new MemoryStream(image[..(image.Length / 3)]);

        _ = await _hasher.ComputeAsync(truncated, CancellationToken.None);
    }

    private async Task AssertConfirmedAsync(byte[] original, byte[] copy)
    {
        var a = await FingerprintAsync(original);
        var b = await FingerprintAsync(copy);

        Assert.InRange(ImageFingerprint.Distance(a.Hash, b.Hash), 0, VisualDuplicateDetector.CandidateDistance);
        Assert.InRange(a.Detail.DistanceTo(b.Detail), 0, VisualDuplicateDetector.ConfirmedDistance);
    }

    private async Task<ImageFingerprint> FingerprintAsync(byte[] image)
    {
        using var stream = new MemoryStream(image);
        var fingerprint = await _hasher.ComputeAsync(stream, CancellationToken.None);
        Assert.NotNull(fingerprint);
        return fingerprint;
    }

    private sealed class ForwardOnlyStream(byte[] content) : MemoryStream(content)
    {
        public override bool CanSeek => false;
    }
}
