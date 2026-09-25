using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Domain.Results;

namespace AppleDrive.UnitTests.Domain;

public sealed class MediaFormatsTests
{
    [Theory]
    [InlineData("IMG_0001.HEIC", MediaType.Image)]
    [InlineData("IMG_0001.heic", MediaType.Image)]
    [InlineData("IMG_0001.JPG", MediaType.Image)]
    [InlineData("IMG_0001.PNG", MediaType.Image)]
    [InlineData("IMG_0001.GIF", MediaType.Image)]
    [InlineData("IMG_0054.WEBP", MediaType.Image)]
    [InlineData("IMG_0001.MOV", MediaType.Video)]
    [InlineData("GZMD8796.MP4", MediaType.Video)]
    [InlineData(".mov", MediaType.Video)]
    [InlineData("mp4", MediaType.Video)]
    [InlineData("IMG_0001.AAE", MediaType.Unknown)]
    [InlineData("notes.txt", MediaType.Unknown)]
    [InlineData("no-extension", MediaType.Unknown)]
    public void Classifies_by_extension(string name, MediaType expected) =>
        Assert.Equal(expected, MediaFormats.GetMediaType(name));

    [Fact]
    public void Returns_mime_types() =>
        Assert.Equal("image/heic", MediaFormats.GetMimeType("IMG_1.HEIC"));
}

public sealed class ResultTests
{
    [Fact]
    public void Success_exposes_value()
    {
        Result<int> result = 42;

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Failure_exposes_error_and_guards_value()
    {
        Result<int> result = new AppError(ErrorKind.DeviceBusy, "busy");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.DeviceBusy, result.Error.Kind);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }
}
