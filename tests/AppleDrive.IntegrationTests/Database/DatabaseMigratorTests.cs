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
    public async Task Frequent_queries_use_an_index(string sql, string expectedIndex)
    {
        var database = new SqliteDatabase(_directory.Combine("index.db"));
        await Migrator(database).MigrateAsync(TestContext.Current.CancellationToken);
        await using var connection = await database.OpenAsync(TestContext.Current.CancellationToken);

        var plan = string.Join(" | ", (await connection.QueryAsync("EXPLAIN QUERY PLAN " + sql)).Select(row => (string)row.detail));

        Assert.Contains(expectedIndex, plan);
        Assert.DoesNotContain("SCAN MediaFiles", plan);
    }

    private static DatabaseMigrator Migrator(SqliteDatabase database) => new(database, NullLogger<DatabaseMigrator>.Instance);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }
}
