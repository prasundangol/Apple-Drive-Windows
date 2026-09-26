using AppleDrive.Application.Services;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Results;
using AppleDrive.Presentation.Formatting;

namespace AppleDrive.UnitTests.Application;

public sealed class TransferHelpersTests
{
    [Theory]
    [InlineData(0x80070070u, false, ErrorKind.DestinationFull)]
    [InlineData(0x80070027u, false, ErrorKind.DestinationFull)]
    [InlineData(0x80070013u, true, ErrorKind.DestinationReadOnly)]
    [InlineData(0x80070015u, true, ErrorKind.DestinationUnavailable)]
    [InlineData(0x80070037u, true, ErrorKind.DestinationUnavailable)]
    [InlineData(0x80070020u, true, ErrorKind.DestinationIo)]
    [InlineData(0x80070020u, false, ErrorKind.DestinationUnavailable)]
    public void Destination_io_errors_are_classified(uint hr, bool rootExists, ErrorKind expected)
    {
        var error = DestinationErrors.FromException(new IOException("test", unchecked((int)hr)), rootExists);

        Assert.Equal(expected, error.Kind);
    }

    [Fact]
    public void Permission_errors_are_access_denied_and_stop_everything()
    {
        var error = DestinationErrors.FromException(new UnauthorizedAccessException(), rootExists: true);

        Assert.Equal(ErrorKind.AccessDenied, error.Kind);
        Assert.True(DestinationErrors.AffectsAllFiles(error));
        Assert.False(DestinationErrors.AffectsAllFiles(new AppError(ErrorKind.DestinationIo, "one file")));
    }

    [Fact]
    public void Speed_needs_a_second_of_data_and_the_estimate_five()
    {
        var rate = new TransferRateEstimator();
        rate.Add(TimeSpan.Zero, 0);
        rate.Add(TimeSpan.FromMilliseconds(500), 5_000_000);
        Assert.Null(rate.BytesPerSecond);

        rate.Add(TimeSpan.FromSeconds(2), 20_000_000);
        Assert.Equal(10_000_000, rate.BytesPerSecond);
        Assert.Null(rate.EstimateRemaining(100_000_000));

        rate.Add(TimeSpan.FromSeconds(6), 60_000_000);
        Assert.Equal(TimeSpan.FromSeconds(4), rate.EstimateRemaining(40_000_000));
    }

    [Fact]
    public void Speed_follows_the_recent_window()
    {
        var rate = new TransferRateEstimator();
        for (var second = 0; second <= 30; second++)
        {
            // 1 MB/s for 20 s, then 10 MB/s.
            var bytes = second <= 20 ? second * 1_000_000L : (20 * 1_000_000L) + ((second - 20) * 10_000_000L);
            rate.Add(TimeSpan.FromSeconds(second), bytes);
        }

        Assert.Equal(10_000_000, rate.BytesPerSecond!.Value, precision: 0);
    }

    [Theory]
    [InlineData(45, "45s")]
    [InlineData(751, "12m 31s")]
    [InlineData(3_900, "1h 5m")]
    [InlineData(0.4, "0s")]
    public void Durations_are_short(double seconds, string expected)
    {
        Assert.Equal(expected, Duration.Format(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Remaining_work_marks_finished_parts_as_already_present()
    {
        var image = Asset("IMG_1.HEIC", MediaType.Image);
        var video = Asset("IMG_1.MOV", MediaType.Video);
        var item = new ItemClassification(
            new MediaItem(image, video),
            [new ComponentClassification(image, AssetStatus.New), new ComponentClassification(video, AssetStatus.New)]);
        var outcome = new ItemTransferOutcome(item,
        [
            new ComponentTransferOutcome(item.Components[0], ItemTransferStatus.Transferred, @"D:\P\IMG_1.HEIC"),
            new ComponentTransferOutcome(item.Components[1], ItemTransferStatus.Failed, Error: new AppError(ErrorKind.DeviceIo, "x")),
        ]);

        var remaining = outcome.ToRemainingWork();

        Assert.Equal(ItemTransferStatus.Failed, outcome.Status);
        Assert.Equal(AssetStatus.ExactDuplicate, remaining.Components[0].Status);
        Assert.Equal(@"D:\P\IMG_1.HEIC", remaining.Components[0].ExistingPath);
        Assert.Equal(AssetStatus.New, remaining.Components[1].Status);
        Assert.Equal(AssetStatus.New, remaining.Status);
    }

    [Fact]
    public void Item_partly_copied_before_a_cancel_counts_as_failed_so_it_is_retried()
    {
        var image = Asset("IMG_1.HEIC", MediaType.Image);
        var video = Asset("IMG_1.MOV", MediaType.Video);
        var item = new ItemClassification(
            new MediaItem(image, video),
            [new ComponentClassification(image, AssetStatus.New), new ComponentClassification(video, AssetStatus.New)]);

        var outcome = new ItemTransferOutcome(item,
        [
            new ComponentTransferOutcome(item.Components[0], ItemTransferStatus.Transferred, @"D:\P\IMG_1.HEIC"),
            new ComponentTransferOutcome(item.Components[1], ItemTransferStatus.NotAttempted),
        ]);

        Assert.Equal(ItemTransferStatus.Failed, outcome.Status);
    }

    private static PhotoAsset Asset(string name, MediaType type) => new()
    {
        Id = name,
        FileName = name,
        SourcePath = "Internal Storage/DCIM/100APPLE/" + name,
        MediaType = type,
        ReportedSize = 1_000,
    };
}
