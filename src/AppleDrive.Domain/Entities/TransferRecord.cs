using AppleDrive.Domain.Enums;

namespace AppleDrive.Domain.Entities;

/// <summary>One file's transfer, as recorded in transfer history.</summary>
public sealed record TransferRecord
{
    public long Id { get; init; }

    public required string SessionId { get; init; }

    public required string SourceAssetId { get; init; }

    public string? SourcePersistentId { get; init; }

    public required string SourceFileName { get; init; }

    public long? ReportedSize { get; init; }

    /// <summary>Temporary file being written; recorded before it is created so it can be cleaned up after a crash.</summary>
    public string? PartialPath { get; init; }

    public string? DestinationPath { get; init; }

    public byte[]? Sha256 { get; init; }

    /// <summary>Bytes actually delivered by the phone.</summary>
    public long? FileSize { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TransferStatus Status { get; init; }

    public string? ErrorKind { get; init; }

    public string? ErrorMessage { get; init; }
}
