using AppleDrive.Domain.Enums;

namespace AppleDrive.Domain.Entities;

/// <summary>A media file in a destination folder, as recorded in the local index.</summary>
public sealed record IndexedMediaFile
{
    public long Id { get; init; }

    public required string FullPath { get; init; }

    public string FileName => Path.GetFileName(FullPath);

    public string Extension => Path.GetExtension(FullPath).ToLowerInvariant();

    public MediaType MediaType { get; init; }

    public long FileSize { get; init; }

    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Last-write time on disk; together with <see cref="FileSize"/> it detects changed files.</summary>
    public DateTimeOffset ModifiedAt { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>When the photo or video was taken, read from its metadata.</summary>
    public DateTimeOffset? CaptureDate { get; init; }

    /// <summary>SHA-256 of the content, or <c>null</c> until it has been needed and computed.</summary>
    public byte[]? Sha256 { get; init; }

    /// <summary>64-bit perceptual fingerprint for images, or <c>null</c> when not computed.</summary>
    public ulong? PerceptualHash { get; init; }

    public DateTimeOffset FirstSeenAt { get; init; }

    public DateTimeOffset LastScannedAt { get; init; }

    /// <summary>False when the file was not found during the last complete scan of its folder.</summary>
    public bool IsAvailable { get; init; } = true;
}
