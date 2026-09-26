using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AppleDrive.Infrastructure.Database;

/// <summary>SQLite-backed <see cref="ITransferRepository"/>. Timestamps are stored as UTC Unix milliseconds.</summary>
public sealed class TransferRepository(SqliteDatabase database) : ITransferRepository
{
    private const string SelectTransfers = """
        SELECT Id, SessionId, SourceAssetId, SourcePersistentId, SourceFileName, ReportedSize, PartialPath,
               DestinationPath, Sha256, FileSize, StartedAt, CompletedAt, Status, ErrorKind, ErrorMessage
        FROM Transfers
        """;

    private const string SelectSessions = """
        SELECT Id, DeviceName, DestinationRoot, StartedAt, CompletedAt, Status,
               TransferredCount, SkippedCount, FailedCount, TransferredBytes
        FROM TransferSessions
        """;

    public Task CreateSessionAsync(TransferSessionRecord session, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            INSERT INTO TransferSessions (Id, DeviceName, DestinationRoot, StartedAt, CompletedAt, Status,
                                          TransferredCount, SkippedCount, FailedCount, TransferredBytes)
            VALUES (@Id, @DeviceName, @DestinationRoot, @StartedAt, @CompletedAt, @Status,
                    @TransferredCount, @SkippedCount, @FailedCount, @TransferredBytes)
            """,
            SessionRow.From(session),
            cancellationToken);

    public Task CompleteSessionAsync(TransferSessionRecord session, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE TransferSessions
            SET CompletedAt = @CompletedAt, Status = @Status, TransferredCount = @TransferredCount,
                SkippedCount = @SkippedCount, FailedCount = @FailedCount, TransferredBytes = @TransferredBytes
            WHERE Id = @Id
            """,
            SessionRow.From(session),
            cancellationToken);

    public async Task<TransferSessionRecord?> GetSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        (await QuerySessionsAsync($"{SelectSessions} WHERE Id = @sessionId", new { sessionId }, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();

    public async Task<long> AddAsync(TransferRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                INSERT INTO Transfers (SessionId, SourceAssetId, SourcePersistentId, SourceFileName, ReportedSize, PartialPath,
                                       DestinationPath, Sha256, FileSize, StartedAt, CompletedAt, Status, ErrorKind, ErrorMessage)
                VALUES (@SessionId, @SourceAssetId, @SourcePersistentId, @SourceFileName, @ReportedSize, @PartialPath,
                        @DestinationPath, @Sha256, @FileSize, @StartedAt, @CompletedAt, @Status, @ErrorKind, @ErrorMessage)
                RETURNING Id
                """,
                TransferRow.From(record),
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new DatabaseException("Recording a transfer failed.", exception);
        }
    }

    public Task CompleteAsync(long id, TransferOutcomeRecord outcome, CancellationToken cancellationToken) =>
        ExecuteAsync(
            """
            UPDATE Transfers
            SET Status = @Status, CompletedAt = @CompletedAt, DestinationPath = @DestinationPath, Sha256 = @Sha256,
                FileSize = @FileSize, ErrorKind = @ErrorKind, ErrorMessage = @ErrorMessage
            WHERE Id = @id
            """,
            new
            {
                id,
                Status = (long)outcome.Status,
                CompletedAt = outcome.CompletedAt.ToUnixTimeMilliseconds(),
                outcome.DestinationPath,
                outcome.Sha256,
                outcome.FileSize,
                outcome.ErrorKind,
                outcome.ErrorMessage,
            },
            cancellationToken);

    public Task<IReadOnlyList<TransferRecord>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken) =>
        QueryAsync($"{SelectTransfers} WHERE SessionId = @sessionId ORDER BY Id", new { sessionId }, cancellationToken);

    public Task<IReadOnlyList<TransferRecord>> GetInProgressAsync(CancellationToken cancellationToken) =>
        QueryAsync($"{SelectTransfers} WHERE Status = 0 ORDER BY Id", new { }, cancellationToken);

    public Task SetTargetAsync(long id, string destinationPath, byte[] sha256, long fileSize, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE Transfers SET DestinationPath = @destinationPath, Sha256 = @sha256, FileSize = @fileSize WHERE Id = @id",
            new { id, destinationPath, sha256, fileSize },
            cancellationToken);

    public async Task<IReadOnlyList<TransferSessionRecord>> GetRunningSessionsAsync(CancellationToken cancellationToken) =>
        await QuerySessionsAsync($"{SelectSessions} WHERE Status = 0 ORDER BY StartedAt", new { }, cancellationToken).ConfigureAwait(false);

    public Task<IReadOnlyList<TransferRecord>> GetCompletedUnderRootAsync(string root, CancellationToken cancellationToken)
    {
        var (low, high) = MediaRepository.PrefixRange(root);
        return QueryAsync(
            $"{SelectTransfers} WHERE Status = 1 AND DestinationPath >= @low AND DestinationPath < @high ORDER BY Id",
            new { low, high },
            cancellationToken);
    }

    private async Task<IReadOnlyList<TransferSessionRecord>> QuerySessionsAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            var rows = await connection.QueryAsync<SessionRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(row => row.ToEntity()).ToList();
        }
        catch (SqliteException exception)
        {
            throw new DatabaseException("Reading transfer history failed.", exception);
        }
    }

    private async Task<IReadOnlyList<TransferRecord>> QueryAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            var rows = await connection.QueryAsync<TransferRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(row => row.ToEntity()).ToList();
        }
        catch (SqliteException exception)
        {
            throw new DatabaseException("Reading transfer history failed.", exception);
        }
    }

    private async Task ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new DatabaseException("Updating transfer history failed.", exception);
        }
    }

    private static long? ToUnix(DateTimeOffset? value) => value?.ToUnixTimeMilliseconds();

    private static DateTimeOffset? FromUnix(long? value) => value is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private sealed class SessionRow
    {
        public string Id { get; init; } = string.Empty;

        public string? DeviceName { get; init; }

        public string DestinationRoot { get; init; } = string.Empty;

        public long StartedAt { get; init; }

        public long? CompletedAt { get; init; }

        public long Status { get; init; }

        public long TransferredCount { get; init; }

        public long SkippedCount { get; init; }

        public long FailedCount { get; init; }

        public long TransferredBytes { get; init; }

        public static SessionRow From(TransferSessionRecord session) => new()
        {
            Id = session.Id,
            DeviceName = session.DeviceName,
            DestinationRoot = session.DestinationRoot,
            StartedAt = session.StartedAt.ToUnixTimeMilliseconds(),
            CompletedAt = ToUnix(session.CompletedAt),
            Status = (long)session.Status,
            TransferredCount = session.TransferredCount,
            SkippedCount = session.SkippedCount,
            FailedCount = session.FailedCount,
            TransferredBytes = session.TransferredBytes,
        };

        public TransferSessionRecord ToEntity() => new()
        {
            Id = Id,
            DeviceName = DeviceName,
            DestinationRoot = DestinationRoot,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(StartedAt),
            CompletedAt = FromUnix(CompletedAt),
            Status = (TransferSessionStatus)Status,
            TransferredCount = (int)TransferredCount,
            SkippedCount = (int)SkippedCount,
            FailedCount = (int)FailedCount,
            TransferredBytes = TransferredBytes,
        };
    }

    private sealed class TransferRow
    {
        public long Id { get; init; }

        public string SessionId { get; init; } = string.Empty;

        public string SourceAssetId { get; init; } = string.Empty;

        public string? SourcePersistentId { get; init; }

        public string SourceFileName { get; init; } = string.Empty;

        public long? ReportedSize { get; init; }

        public string? PartialPath { get; init; }

        public string? DestinationPath { get; init; }

        public byte[]? Sha256 { get; init; }

        public long? FileSize { get; init; }

        public long StartedAt { get; init; }

        public long? CompletedAt { get; init; }

        public long Status { get; init; }

        public string? ErrorKind { get; init; }

        public string? ErrorMessage { get; init; }

        public static TransferRow From(TransferRecord record) => new()
        {
            SessionId = record.SessionId,
            SourceAssetId = record.SourceAssetId,
            SourcePersistentId = record.SourcePersistentId,
            SourceFileName = record.SourceFileName,
            ReportedSize = record.ReportedSize,
            PartialPath = record.PartialPath,
            DestinationPath = record.DestinationPath,
            Sha256 = record.Sha256,
            FileSize = record.FileSize,
            StartedAt = record.StartedAt.ToUnixTimeMilliseconds(),
            CompletedAt = ToUnix(record.CompletedAt),
            Status = (long)record.Status,
            ErrorKind = record.ErrorKind,
            ErrorMessage = record.ErrorMessage,
        };

        public TransferRecord ToEntity() => new()
        {
            Id = Id,
            SessionId = SessionId,
            SourceAssetId = SourceAssetId,
            SourcePersistentId = SourcePersistentId,
            SourceFileName = SourceFileName,
            ReportedSize = ReportedSize,
            PartialPath = PartialPath,
            DestinationPath = DestinationPath,
            Sha256 = Sha256,
            FileSize = FileSize,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(StartedAt),
            CompletedAt = FromUnix(CompletedAt),
            Status = (TransferStatus)Status,
            ErrorKind = ErrorKind,
            ErrorMessage = ErrorMessage,
        };
    }
}
