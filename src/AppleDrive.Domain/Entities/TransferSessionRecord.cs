using AppleDrive.Domain.Enums;

namespace AppleDrive.Domain.Entities;

/// <summary>One transfer run, as recorded in transfer history.</summary>
public sealed record TransferSessionRecord
{
    public required string Id { get; init; }

    public string? DeviceName { get; init; }

    public required string DestinationRoot { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TransferSessionStatus Status { get; init; }

    public int TransferredCount { get; init; }

    public int SkippedCount { get; init; }

    public int FailedCount { get; init; }

    public long TransferredBytes { get; init; }
}
