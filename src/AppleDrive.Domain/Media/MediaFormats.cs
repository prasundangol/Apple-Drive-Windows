using AppleDrive.Domain.Enums;

namespace AppleDrive.Domain.Media;

/// <summary>Known media file extensions and their types.</summary>
public static class MediaFormats
{
    private static readonly Dictionary<string, (MediaType Type, string Mime)> Formats =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = (MediaType.Image, "image/jpeg"),
            [".jpeg"] = (MediaType.Image, "image/jpeg"),
            [".heic"] = (MediaType.Image, "image/heic"),
            [".heif"] = (MediaType.Image, "image/heif"),
            [".hif"] = (MediaType.Image, "image/heif"),
            [".png"] = (MediaType.Image, "image/png"),
            [".gif"] = (MediaType.Image, "image/gif"),
            [".tif"] = (MediaType.Image, "image/tiff"),
            [".tiff"] = (MediaType.Image, "image/tiff"),
            [".dng"] = (MediaType.Image, "image/x-adobe-dng"),
            [".webp"] = (MediaType.Image, "image/webp"),
            [".mov"] = (MediaType.Video, "video/quicktime"),
            [".mp4"] = (MediaType.Video, "video/mp4"),
            [".m4v"] = (MediaType.Video, "video/x-m4v"),
            [".3gp"] = (MediaType.Video, "video/3gpp"),
        };

    /// <summary>Returns the media type for a file name or extension, or <see cref="MediaType.Unknown"/>.</summary>
    public static MediaType GetMediaType(string fileNameOrExtension) =>
        Formats.TryGetValue(NormalizeExtension(fileNameOrExtension), out var format) ? format.Type : MediaType.Unknown;

    /// <summary>Returns the MIME type for a file name or extension, or <c>null</c>.</summary>
    public static string? GetMimeType(string fileNameOrExtension) =>
        Formats.TryGetValue(NormalizeExtension(fileNameOrExtension), out var format) ? format.Mime : null;

    public static bool IsSupported(string fileNameOrExtension) =>
        GetMediaType(fileNameOrExtension) != MediaType.Unknown;

    private static string NormalizeExtension(string value)
    {
        var extension = Path.GetExtension(value);
        if (!string.IsNullOrEmpty(extension))
        {
            return extension;
        }

        return value.StartsWith('.') ? value : "." + value;
    }
}
