using Microsoft.Data.Sqlite;

namespace AppleDrive.Infrastructure.Database;

/// <summary>Opens connections to the local media index database.</summary>
public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    public SqliteDatabase(string databaseFile)
    {
        DatabaseFile = databaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string DatabaseFile { get; }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
