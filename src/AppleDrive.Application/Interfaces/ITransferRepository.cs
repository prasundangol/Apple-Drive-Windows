using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;

namespace AppleDrive.Application.Interfaces;

/// <summary>Persistence for transfer history: one session per run, one record per file.</summary>
public interface ITransferRepository
{
    Task CreateSessionAsync(TransferSessionRecord session, CancellationToken cancellationToken);

    /// <summary>Stores the final status and totals of a session.</summary>
    Task CompleteSessionAsync(TransferSessionRecord session, CancellationToken cancellationToken);

    Task<TransferSessionRecord?> GetSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Records a file transfer that is starting and returns its id.</summary>
    Task<long> AddAsync(TransferRecord record, CancellationToken cancellationToken);

    /// <summary>Stores the outcome of a transfer started with <see cref="AddAsync"/>.</summary>
    Task CompleteAsync(long id, TransferOutcomeRecord outcome, CancellationToken cancellationToken);

    /// <summary>
    /// Records the final path and verified content of a transfer just before its file is renamed
    /// into place, so a crash between the rename and <see cref="CompleteAsync"/> can be recovered.
    /// </summary>
    Task SetTargetAsync(long id, string destinationPath, byte[] sha256, long fileSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<TransferRecord>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Transfers still marked <see cref="TransferStatus.InProgress"/>: the app stopped while they ran.</summary>
    Task<IReadOnlyList<TransferRecord>> GetInProgressAsync(CancellationToken cancellationToken);

    /// <summary>The most recent sessions, newest first.</summary>
    Task<IReadOnlyList<TransferSessionRecord>> GetRecentSessionsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>Sessions still marked <see cref="TransferSessionStatus.Running"/>.</summary>
    Task<IReadOnlyList<TransferSessionRecord>> GetRunningSessionsAsync(CancellationToken cancellationToken);

    /// <summary>Completed transfers whose destination file is inside <paramref name="root"/>.</summary>
    Task<IReadOnlyList<TransferRecord>> GetCompletedUnderRootAsync(string root, CancellationToken cancellationToken);
}

/// <summary>The fields set when a transfer finishes.</summary>
public sealed record TransferOutcomeRecord(
    TransferStatus Status,
    DateTimeOffset CompletedAt,
    string? DestinationPath = null,
    byte[]? Sha256 = null,
    long? FileSize = null,
    string? ErrorKind = null,
    string? ErrorMessage = null);
