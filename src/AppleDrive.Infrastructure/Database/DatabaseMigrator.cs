using AppleDrive.Application.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MigrationList = AppleDrive.Infrastructure.Database.Migrations.Migrations;

namespace AppleDrive.Infrastructure.Database;

/// <summary>
/// Upgrades the database schema in place, tracking the version in <c>PRAGMA user_version</c>.
/// Each migration runs in its own transaction, and an existing database is backed up before
/// the first migration is applied, so an upgrade can never lose the media index.
/// </summary>
public sealed class DatabaseMigrator(SqliteDatabase database, ILogger<DatabaseMigrator> logger)
{
    public static int LatestVersion => MigrationList.LatestVersion;

    /// <summary>Applies pending migrations. Returns the resulting schema version.</summary>
    public async Task<int> MigrateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);

            var current = await GetVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (current > LatestVersion)
            {
                // Written by a newer app version; refuse rather than risk misreading it.
                throw new DatabaseException(
                    $"The media index was created by a newer version of Apple Drive (schema {current}, this version supports {LatestVersion}).",
                    new InvalidOperationException("Unsupported schema version."));
            }

            var pending = MigrationList.All.Where(migration => migration.Version > current).ToList();
            if (pending.Count == 0)
            {
                return current;
            }

            if (current > 0)
            {
                BackUp(connection, current);
            }

            foreach (var migration in pending)
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, migration.Sql, cancellationToken, transaction).ConfigureAwait(false);
                await ExecuteAsync(connection, $"PRAGMA user_version = {migration.Version};", cancellationToken, transaction).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Applied database migration {Version}: {Description}", migration.Version, migration.Description);
            }

            return LatestVersion;
        }
        catch (SqliteException exception)
        {
            throw new DatabaseException("The media index database could not be opened or upgraded.", exception);
        }
    }

    private void BackUp(SqliteConnection connection, int version)
    {
        var backupPath = $"{database.DatabaseFile}.v{version}.bak";
        using var backup = new SqliteConnection($"Data Source={backupPath};Pooling=False");
        backup.Open();
        connection.BackupDatabase(backup);
        logger.LogInformation("Backed up media index (schema {Version}) before upgrading", version);
    }

    private static async Task<int> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
