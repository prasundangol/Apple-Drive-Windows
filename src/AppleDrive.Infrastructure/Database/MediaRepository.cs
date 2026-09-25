using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AppleDrive.Infrastructure.Database;

/// <summary>SQLite-backed <see cref="IMediaRepository"/>. Timestamps are stored as UTC Unix milliseconds.</summary>
public sealed class MediaRepository(SqliteDatabase database) : IMediaRepository
{
    private const int IdChunkSize = 500;

    private const string SelectColumns = """
        SELECT Id, FullPath, MediaType, FileSize, CreatedAt, ModifiedAt, Width, Height, CaptureDate,
               Sha256, PerceptualHash, FirstSeenAt, LastScannedAt, IsAvailable
        FROM MediaFiles
        """;

    public Task<IReadOnlyList<IndexedMediaFile>> GetUnderRootAsync(string root, CancellationToken cancellationToken)
    {
        var (low, high) = PrefixRange(root);
        return QueryAsync($"{SelectColumns} WHERE FullPath >= @low AND FullPath < @high", new { low, high }, cancellationToken);
    }

    public async Task<IndexedMediaFile?> GetByPathAsync(string fullPath, CancellationToken cancellationToken) =>
        (await QueryAsync($"{SelectColumns} WHERE FullPath = @fullPath", new { fullPath }, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault();

    public Task<IReadOnlyList<IndexedMediaFile>> GetAvailableBySizeAsync(long fileSize, CancellationToken cancellationToken) =>
        QueryAsync($"{SelectColumns} WHERE FileSize = @fileSize AND IsAvailable = 1", new { fileSize }, cancellationToken);

    public Task<IReadOnlyList<IndexedMediaFile>> GetAvailableBySha256Async(byte[] sha256, CancellationToken cancellationToken) =>
        QueryAsync($"{SelectColumns} WHERE Sha256 = @sha256 AND IsAvailable = 1", new { sha256 }, cancellationToken);

    public Task UpsertAsync(IReadOnlyCollection<IndexedMediaFile> files, CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO MediaFiles (FullPath, MediaType, FileSize, CreatedAt, ModifiedAt, Width, Height, CaptureDate,
                                    Sha256, PerceptualHash, FirstSeenAt, LastScannedAt, IsAvailable)
            VALUES (@FullPath, @MediaType, @FileSize, @CreatedAt, @ModifiedAt, @Width, @Height, @CaptureDate,
                    @Sha256, @PerceptualHash, @FirstSeenAt, @LastScannedAt, @IsAvailable)
            ON CONFLICT (FullPath) DO UPDATE SET
                MediaType = excluded.MediaType,
                FileSize = excluded.FileSize,
                CreatedAt = excluded.CreatedAt,
                ModifiedAt = excluded.ModifiedAt,
                Width = excluded.Width,
                Height = excluded.Height,
                CaptureDate = excluded.CaptureDate,
                Sha256 = excluded.Sha256,
                PerceptualHash = excluded.PerceptualHash,
                FirstSeenAt = MIN(MediaFiles.FirstSeenAt, excluded.FirstSeenAt),
                LastScannedAt = excluded.LastScannedAt,
                IsAvailable = excluded.IsAvailable;
            """;

        return InTransactionAsync(
            (connection, transaction) => connection.ExecuteAsync(new CommandDefinition(
                sql, files.Select(MediaRow.From).ToList(), transaction, cancellationToken: cancellationToken)),
            cancellationToken);
    }

    public Task SetSha256Async(long id, byte[] sha256, CancellationToken cancellationToken) =>
        ExecuteAsync("UPDATE MediaFiles SET Sha256 = @sha256 WHERE Id = @id", new { id, sha256 }, cancellationToken);

    public Task SetPerceptualHashAsync(long id, ulong perceptualHash, CancellationToken cancellationToken) =>
        ExecuteAsync(
            "UPDATE MediaFiles SET PerceptualHash = @hash WHERE Id = @id",
            new { id, hash = unchecked((long)perceptualHash) },
            cancellationToken);

    public Task MarkUnavailableAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken) =>
        InTransactionAsync(
            async (connection, transaction) =>
            {
                foreach (var chunk in ids.Chunk(IdChunkSize))
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE MediaFiles SET IsAvailable = 0 WHERE Id IN @chunk",
                        new { chunk },
                        transaction,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                }

                return ids.Count;
            },
            cancellationToken);

    public Task DeleteAsync(long id, CancellationToken cancellationToken) =>
        ExecuteAsync("DELETE FROM MediaFiles WHERE Id = @id", new { id }, cancellationToken);

    public Task TouchScannedAsync(string root, DateTimeOffset scannedAt, CancellationToken cancellationToken)
    {
        var (low, high) = PrefixRange(root);
        return ExecuteAsync(
            "UPDATE MediaFiles SET LastScannedAt = @at WHERE FullPath >= @low AND FullPath < @high AND IsAvailable = 1",
            new { at = scannedAt.ToUnixTimeMilliseconds(), low, high },
            cancellationToken);
    }

    /// <summary>
    /// Range covering every path inside <paramref name="root"/>: [root\ , root]) — ']' sorts
    /// immediately after '\'. A range (unlike LIKE with escapes) can use the path index.
    /// </summary>
    internal static (string Low, string High) PrefixRange(string root)
    {
        var prefix = root.EndsWith('\\') ? root : root + '\\';
        return (prefix, prefix[..^1] + ']');
    }

    private async Task<IReadOnlyList<IndexedMediaFile>> QueryAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            var rows = await connection.QueryAsync<MediaRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.Select(row => row.ToEntity()).ToList();
        }
        catch (SqliteException exception)
        {
            throw new DatabaseException("Reading the media index failed.", exception);
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
            throw new DatabaseException("Updating the media index failed.", exception);
        }
    }

    private async Task InTransactionAsync(
        Func<SqliteConnection, SqliteTransaction, Task<int>> work,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await work(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new DatabaseException("Updating the media index failed.", exception);
        }
    }

    /// <summary>Flat row shape matching the table columns.</summary>
    private sealed class MediaRow
    {
        public long Id { get; init; }

        public string FullPath { get; init; } = string.Empty;

        public long MediaType { get; init; }

        public long FileSize { get; init; }

        public long? CreatedAt { get; init; }

        public long ModifiedAt { get; init; }

        public long? Width { get; init; }

        public long? Height { get; init; }

        public long? CaptureDate { get; init; }

        public byte[]? Sha256 { get; init; }

        public long? PerceptualHash { get; init; }

        public long FirstSeenAt { get; init; }

        public long LastScannedAt { get; init; }

        public long IsAvailable { get; init; }

        public static MediaRow From(IndexedMediaFile file) => new()
        {
            FullPath = file.FullPath,
            MediaType = (long)file.MediaType,
            FileSize = file.FileSize,
            CreatedAt = file.CreatedAt?.ToUnixTimeMilliseconds(),
            ModifiedAt = file.ModifiedAt.ToUnixTimeMilliseconds(),
            Width = file.Width,
            Height = file.Height,
            CaptureDate = file.CaptureDate?.ToUnixTimeMilliseconds(),
            Sha256 = file.Sha256,
            PerceptualHash = file.PerceptualHash is { } hash ? unchecked((long)hash) : null,
            FirstSeenAt = file.FirstSeenAt.ToUnixTimeMilliseconds(),
            LastScannedAt = file.LastScannedAt.ToUnixTimeMilliseconds(),
            IsAvailable = file.IsAvailable ? 1 : 0,
        };

        public IndexedMediaFile ToEntity() => new()
        {
            Id = Id,
            FullPath = FullPath,
            MediaType = (MediaType)MediaType,
            FileSize = FileSize,
            CreatedAt = CreatedAt is { } created ? DateTimeOffset.FromUnixTimeMilliseconds(created) : null,
            ModifiedAt = DateTimeOffset.FromUnixTimeMilliseconds(ModifiedAt),
            Width = Width is { } width ? (int)width : null,
            Height = Height is { } height ? (int)height : null,
            CaptureDate = CaptureDate is { } capture ? DateTimeOffset.FromUnixTimeMilliseconds(capture) : null,
            Sha256 = Sha256,
            PerceptualHash = PerceptualHash is { } hash ? unchecked((ulong)hash) : null,
            FirstSeenAt = DateTimeOffset.FromUnixTimeMilliseconds(FirstSeenAt),
            LastScannedAt = DateTimeOffset.FromUnixTimeMilliseconds(LastScannedAt),
            IsAvailable = IsAvailable != 0,
        };
    }
}
