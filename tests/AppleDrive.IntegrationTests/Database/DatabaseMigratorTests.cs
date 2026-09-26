using AppleDrive.Application.Interfaces;
using AppleDrive.Infrastructure.Database;
using AppleDrive.Testing;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Database;

public sealed class DatabaseMigratorTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    [Fact]
    public async Task Creates_schema_on_a_new_database()
    {
        var database = new SqliteDatabase(_directory.Combine("index.db"));

        var version = await Migrator(database).MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseMigrator.LatestVersion, version);
        await using var connection = await database.OpenAsync(TestContext.Current.CancellationToken);
        var tables = await connection.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type = 'table'");
        Assert.Contains("MediaFiles", tables);
        Assert.Contains("TransferSessions", tables);
        Assert.Contains("Transfers", tables);
        Assert.Equal("wal", await connection.ExecuteScalarAsync<string>("PRAGMA journal_mode;"));
    }

    [Fact]
    public async Task Is_idempotent_and_keeps_data()
    {
        var database = new SqliteDatabase(_directory.Combine("index.db"));
        await Migrator(database).MigrateAsync(TestContext.Current.CancellationToken);
        await using (var connection = await database.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "INSERT INTO MediaFiles (FullPath, MediaType, FileSize, ModifiedAt, FirstSeenAt, LastScannedAt) VALUES ('D:\\a.jpg', 1, 10, 0, 0, 0)");
        }

        await Migrator(database).MigrateAsync(TestContext.Current.CancellationToken);

        await using var check = await database.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaFiles"));
    }

    [Fact]
    public async Task Upgrades_a_version_1_database_keeping_its_data_and_a_backup()
    {
        var path = _directory.Combine("index.db");
        var database = new SqliteDatabase(path);
        await using (var connection = await database.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(Infrastructure.Database.Migrations.Migrations.All[0].Sql);
            await connection.ExecuteAsync("PRAGMA user_version = 1;");
            await connection.ExecuteAsync(
                "INSERT INTO MediaFiles (FullPath, MediaType, FileSize, ModifiedAt, FirstSeenAt, LastScannedAt) VALUES ('D:\\a.jpg', 1, 10, 0, 0, 0)");
        }

        var version = await Migrator(database).MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseMigrator.LatestVersion, version);
        Assert.True(File.Exists(path + ".v1.bak"));
        await using var check = await database.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MediaFiles"));
        Assert.Equal(0, await check.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Transfers"));
    }

    [Fact]
    public async Task Refuses_a_database_from_a_newer_version_without_touching_it()
    {
        var path = _directory.Combine("index.db");
        var database = new SqliteDatabase(path);
        await using (var connection = await database.OpenAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync($"PRAGMA user_version = {DatabaseMigrator.LatestVersion + 5};");
        }

        await Assert.ThrowsAsync<DatabaseException>(() => Migrator(database).MigrateAsync(TestContext.Current.CancellationToken));

        await using var check = await database.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal(DatabaseMigrator.LatestVersion + 5, await check.ExecuteScalarAsync<int>("PRAGMA user_version;"));
    }

    [Theory]
    [InlineData("SELECT Id FROM MediaFiles WHERE FileSize = 100 AND IsAvailable = 1", "IX_MediaFiles_FileSize")]
    [InlineData("SELECT Id FROM MediaFiles WHERE Sha256 = x'00' AND IsAvailable = 1", "IX_MediaFiles_Sha256")]
    [InlineData("SELECT Id FROM MediaFiles WHERE FullPath = 'D:\\a.jpg'", "UX_MediaFiles_FullPath")]
    [InlineData("SELECT Id FROM MediaFiles WHERE FullPath >= 'D:\\Photos\\' AND FullPath < 'D:\\Photos]'", "UX_MediaFiles_FullPath")]
    [InlineData("SELECT Id FROM Transfers WHERE SessionId = 's' ORDER BY Id", "IX_Transfers_SessionId_Status")]
    [InlineData("SELECT Id FROM Transfers WHERE SessionId = 's' AND Status = 2", "IX_Transfers_SessionId_Status")]
    [InlineData("SELECT Id FROM Transfers WHERE Status = 0 ORDER BY Id", "IX_Transfers_InProgress")]
    [InlineData("SELECT Id FROM TransferSessions ORDER BY StartedAt DESC LIMIT 200", "IX_TransferSessions_StartedAt")]
    public async Task Frequent_queries_use_an_index(string sql, string expectedIndex)
    {
        var database = new SqliteDatabase(_directory.Combine("index.db"));
        await Migrator(database).MigrateAsync(TestContext.Current.CancellationToken);
        await using var connection = await database.OpenAsync(TestContext.Current.CancellationToken);

        var plan = string.Join(" | ", (await connection.QueryAsync("EXPLAIN QUERY PLAN " + sql)).Select(row => (string)row.detail));

        Assert.Contains(expectedIndex, plan);
        // A bare "SCAN <table>" (no index) would read every row.
        Assert.DoesNotMatch(@"SCAN \w+( \||$)", plan);
    }

    private static DatabaseMigrator Migrator(SqliteDatabase database) => new(database, NullLogger<DatabaseMigrator>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }
}
