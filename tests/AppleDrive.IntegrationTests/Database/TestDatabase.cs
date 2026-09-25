using AppleDrive.Infrastructure.Database;
using AppleDrive.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Database;

/// <summary>A migrated SQLite database in a temporary folder.</summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private readonly TemporaryDirectory _directory = new();

    private TestDatabase()
    {
        Database = new SqliteDatabase(_directory.Combine("index.db"));
        Repository = new MediaRepository(Database);
    }

    public SqliteDatabase Database { get; }

    public MediaRepository Repository { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var database = new TestDatabase();
        await new DatabaseMigrator(database.Database, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
        return database;
    }

    public ValueTask DisposeAsync()
    {
        // Pooled connections keep the file open; release them so the folder can be deleted.
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
        return ValueTask.CompletedTask;
    }
}
