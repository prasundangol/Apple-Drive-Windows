using AppleDrive.Domain.Enums;

namespace AppleDrive.Domain.Entities;

/// <summary>A single media file on the source device.</summary>
/// <remarks>
/// <see cref="FileName"/> is not unique on an iPhone (IMG_0001 repeats across DCIM folders),
/// so identity for de-duplication is always content-based, never name-based.
/// </remarks>
public sealed class PhotoAsset
{
    /// <summary>Source-specific object identifier, valid for the current connection only.</summary>
    public required string Id { get; init; }

    /// <summary>Identifier the source claims is stable across connections, when available.</summary>
    public string? PersistentId { get; init; }

    public required string FileName { get; init; }

    /// <summary>Display path on the device, e.g. "Internal Storage/DCIM/100APPLE/IMG_0001.HEIC".</summary>
    public string? SourcePath { get; init; }

    public MediaType MediaType { get; init; }

    /// <summary>
    /// Size reported by the device. Treat as a hint only: an iPhone converting HEIC to JPEG
    /// during transfer can deliver a different number of bytes.
    /// </summary>
    public long? ReportedSize { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    public DateTimeOffset? ModifiedAt { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    public string? MimeType { get; init; }

    public string Extension => Path.GetExtension(FileName).ToLowerInvariant();

    /// <summary>File name without extension, used to pair Live Photo components.</summary>
    public string BaseName => Path.GetFileNameWithoutExtension(FileName);

    /// <summary>Folder portion of <see cref="SourcePath"/>, or empty when unknown.</summary>
    public string SourceFolder
    {
        get
        {
            if (string.IsNullOrEmpty(SourcePath))
            {
                return string.Empty;
            }

            var separator = SourcePath.LastIndexOf('/');
            return separator < 0 ? string.Empty : SourcePath[..separator];
        }
    }
}
