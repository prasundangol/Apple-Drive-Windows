using System.Security.Cryptography;
using AppleDrive.Application.Interfaces;
using AppleDrive.Infrastructure.Metadata;
using AppleDrive.Testing;

namespace AppleDrive.UnitTests.Infrastructure;

public sealed class CaptureDateReaderTests
{
    private static readonly TimeSpan Nepal = TimeSpan.FromMinutes(345);

    [Fact]
    public void Jpeg_exif_date_with_offset()
    {
        var date = Read(MediaFixtures.Jpeg(MediaFixtures.Exif("2024:03:15 09:30:00", "+05:45")));

        Assert.Equal(new DateTimeOffset(2024, 3, 15, 9, 30, 0, Nepal), date!.Value);
        Assert.Equal(new DateTime(2024, 3, 15, 9, 30, 0), date.Value.DateTime);
        Assert.True(date.HasKnownOffset);
    }

    [Fact]
    public void Jpeg_exif_date_without_offset_keeps_the_wall_clock_time()
    {
        var date = Read(MediaFixtures.Jpeg(MediaFixtures.Exif("2019:12:31 23:59:58")));

        Assert.Equal(new DateTime(2019, 12, 31, 23, 59, 58), date!.Value.DateTime);
        Assert.False(date.HasKnownOffset);
    }

    [Fact]
    public void Big_endian_exif_is_read()
    {
        var date = Read(MediaFixtures.Jpeg(MediaFixtures.Exif("2021:06:01 12:00:00", "-07:00", bigEndian: true)));

        Assert.Equal(new DateTimeOffset(2021, 6, 1, 12, 0, 0, TimeSpan.FromHours(-7)), date!.Value);
    }

    [Fact]
    public void Falls_back_to_the_file_date_time_tag()
    {
        var date = Read(MediaFixtures.Jpeg(MediaFixtures.Exif(dateTimeOriginal: null, dateTime: "2018:02:03 04:05:06")));

        Assert.Equal(new DateTime(2018, 2, 3, 4, 5, 6), date!.Value.DateTime);
    }

    [Fact]
    public void Heic_exif_item_is_found_through_the_meta_box()
    {
        var date = Read(MediaFixtures.Heic(MediaFixtures.Exif("2024:03:31 23:30:00", "-08:00")));

        Assert.Equal(new DateTimeOffset(2024, 3, 31, 23, 30, 0, TimeSpan.FromHours(-8)), date!.Value);
    }

    [Fact]
    public void Heic_without_exif_has_no_date()
    {
        Assert.Null(Read(MediaFixtures.Heic(exif: null)));
    }

    [Fact]
    public void QuickTime_prefers_the_apple_creation_date_with_its_offset()
    {
        var date = Read(MediaFixtures.QuickTime(new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc), "2024-12-31T22:00:00-0500"));

        Assert.Equal(new DateTimeOffset(2024, 12, 31, 22, 0, 0, TimeSpan.FromHours(-5)), date!.Value);
        Assert.True(date.HasKnownOffset);
    }

    [Fact]
    public void QuickTime_falls_back_to_the_movie_header_time_in_utc()
    {
        var utc = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);

        var date = Read(MediaFixtures.QuickTime(utc));

        Assert.Equal(new DateTimeOffset(utc), date!.Value);
        Assert.Equal(utc.ToLocalTime(), date.Value.DateTime);
    }

    [Fact]
    public void QuickTime_without_a_date_has_none()
    {
        Assert.Null(Read(MediaFixtures.QuickTime(null)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(100)]
    [InlineData(5_000)]
    public void Garbage_and_empty_files_have_no_date(int length)
    {
        Assert.Null(Read(RandomNumberGenerator.GetBytes(length).Select(b => b == 0xFF ? (byte)0 : b).ToArray()));
    }

    [Fact]
    public void Truncated_files_have_no_date_instead_of_failing()
    {
        var full = MediaFixtures.Heic(MediaFixtures.Exif("2024:03:31 23:30:00"));

        for (var length = 12; length < full.Length; length += 37)
        {
            _ = Read(full[..length]); // Must not throw.
        }

        Assert.Null(Read(MediaFixtures.Jpeg(MediaFixtures.Exif("2024:03:31 23:30:00"))[..30]));
    }

    [Theory]
    [InlineData("0000:00:00 00:00:00")]
    [InlineData("    :  :     :  :  ")]
    [InlineData("2024-03-15 09:30:00")]
    public void Unset_or_malformed_exif_dates_are_ignored(string value)
    {
        Assert.Null(Read(MediaFixtures.Jpeg(MediaFixtures.Exif(value))));
    }

    [Theory]
    [InlineData("2024-12-31T22:00:00-0500", -300)]
    [InlineData("2024-12-31T22:00:00+05:45", 345)]
    [InlineData("2024-12-31T22:00:00Z", 0)]
    public void Parses_iso_dates_as_apple_writes_them(string text, int offsetMinutes)
    {
        var date = CaptureDateReader.ParseIsoDate(text);

        Assert.Equal(new DateTimeOffset(2024, 12, 31, 22, 0, 0, TimeSpan.FromMinutes(offsetMinutes)), date!.Value);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(6, false)]
    [InlineData(8, true)]
    [InlineData(3, true)]
    public void Reads_the_orientation_of_a_jpeg(int orientation, bool bigEndian)
    {
        var jpeg = MediaFixtures.Jpeg(MediaFixtures.Exif("2024:01:01 10:00:00", bigEndian: bigEndian, orientation: (ushort)orientation));

        Assert.Equal(orientation, CaptureDateReader.ReadOrientation(new MemoryStream(jpeg)));
    }

    [Fact]
    public void Reads_the_orientation_of_a_heic()
    {
        var heic = MediaFixtures.Heic(MediaFixtures.Exif("2024:01:01 10:00:00", orientation: 6));

        Assert.Equal(6, CaptureDateReader.ReadOrientation(new MemoryStream(heic)));
    }

    [Fact]
    public void Images_without_an_orientation_tag_have_none()
    {
        Assert.Null(CaptureDateReader.ReadOrientation(new MemoryStream(MediaFixtures.Jpeg(MediaFixtures.Exif("2024:01:01 10:00:00")))));
        Assert.Null(CaptureDateReader.ReadOrientation(new MemoryStream(MediaFixtures.Jpeg(null))));
        Assert.Null(CaptureDateReader.ReadOrientation(new MemoryStream(MediaFixtures.QuickTime(DateTime.UtcNow))));
    }

    [Fact]
    public void Orientation_tag_does_not_disturb_the_capture_date()
    {
        var date = Read(MediaFixtures.Jpeg(MediaFixtures.Exif("2024:03:15 09:30:00", "+05:45", orientation: 6)));

        Assert.Equal(new DateTimeOffset(2024, 3, 15, 9, 30, 0, Nepal), date!.Value);
    }

    [Fact]
    public async Task Reads_from_a_file_on_disk()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.Combine("IMG_0001.HEIC");
        await File.WriteAllBytesAsync(path, MediaFixtures.Heic(MediaFixtures.Exif("2024:03:15 09:30:00", "+05:45")), TestContext.Current.CancellationToken);

        var date = await new CaptureDateReader().ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(new DateTimeOffset(2024, 3, 15, 9, 30, 0, Nepal), date!.Value);
    }

    private static CaptureDate? Read(byte[] content)
    {
        using var stream = new MemoryStream(content);
        return CaptureDateReader.Read(stream);
    }
}
