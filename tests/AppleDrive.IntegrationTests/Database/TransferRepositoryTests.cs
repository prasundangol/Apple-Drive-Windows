using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Infrastructure.Database;

namespace AppleDrive.IntegrationTests.Database;

public sealed class TransferRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 10, 0, 0, TimeSpan.Zero);
    private TestDatabase _db = null!;
    private TransferRepository _repository = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _repository = new TransferRepository(_db.Database);
    }

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Session_is_created_then_completed_with_totals()
    {
        await _repository.CreateSessionAsync(Session("s1"), Ct);

        await _repository.CompleteSessionAsync(
            Session("s1") with
            {
                CompletedAt = Now.AddMinutes(5),
                Status = TransferSessionStatus.Stopped,
                TransferredCount = 10,
                SkippedCount = 3,
                FailedCount = 1,
                TransferredBytes = 123_456_789_012,
            },
            Ct);

        var stored = await _repository.GetSessionAsync("s1", Ct);
        Assert.Equal(TransferSessionStatus.Stopped, stored!.Status);
        Assert.Equal(Now.AddMinutes(5), stored.CompletedAt);
        Assert.Equal(10, stored.TransferredCount);
        Assert.Equal(3, stored.SkippedCount);
        Assert.Equal(1, stored.FailedCount);
        Assert.Equal(123_456_789_012, stored.TransferredBytes);
        Assert.Equal("Test iPhone", stored.DeviceName);
    }

    [Fact]
    public async Task Transfer_is_recorded_in_progress_then_completed()
    {
        await _repository.CreateSessionAsync(Session("s1"), Ct);
        var id = await _repository.AddAsync(Transfer("s1", "IMG_1.HEIC"), Ct);
        Assert.Single(await _repository.GetInProgressAsync(Ct));
        var sha = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        await _repository.CompleteAsync(id, new TransferOutcomeRecord(TransferStatus.Completed, Now.AddSeconds(3), @"D:\Photos\IMG_1.HEIC", sha, 4_000), Ct);

        var stored = Assert.Single(await _repository.GetBySessionAsync("s1", Ct));
        Assert.Equal(TransferStatus.Completed, stored.Status);
        Assert.Equal(@"D:\Photos\IMG_1.HEIC", stored.DestinationPath);
        Assert.Equal(sha, stored.Sha256);
        Assert.Equal(4_000, stored.FileSize);
        Assert.Equal(@"D:\Photos\IMG_1.HEIC.1234abcd.partial", stored.PartialPath);
        Assert.Equal("pid-1", stored.SourcePersistentId);
        Assert.Equal(Now.AddSeconds(3), stored.CompletedAt);
        Assert.Empty(await _repository.GetInProgressAsync(Ct));
    }

    [Fact]
    public async Task Failure_keeps_the_error()
    {
        await _repository.CreateSessionAsync(Session("s1"), Ct);
        var id = await _repository.AddAsync(Transfer("s1", "IMG_1.HEIC"), Ct);

        await _repository.CompleteAsync(id, new TransferOutcomeRecord(TransferStatus.Failed, Now, ErrorKind: "DeviceIo", ErrorMessage: "Reading failed"), Ct);

        var stored = Assert.Single(await _repository.GetBySessionAsync("s1", Ct));
        Assert.Equal(TransferStatus.Failed, stored.Status);
        Assert.Equal("DeviceIo", stored.ErrorKind);
        Assert.Equal("Reading failed", stored.ErrorMessage);
        Assert.Null(stored.DestinationPath);
    }

    [Fact]
    public async Task Transfers_are_listed_per_session_in_order()
    {
        await _repository.CreateSessionAsync(Session("s1"), Ct);
        await _repository.CreateSessionAsync(Session("s2"), Ct);
        await _repository.AddAsync(Transfer("s1", "a.jpg"), Ct);
        await _repository.AddAsync(Transfer("s2", "b.jpg"), Ct);
        await _repository.AddAsync(Transfer("s1", "c.jpg"), Ct);

        var files = (await _repository.GetBySessionAsync("s1", Ct)).Select(t => t.SourceFileName);

        Assert.Equal(["a.jpg", "c.jpg"], files);
    }

    [Fact]
    public async Task Transfer_requires_an_existing_session()
    {
        await Assert.ThrowsAsync<DatabaseException>(() => _repository.AddAsync(Transfer("missing", "a.jpg"), Ct));
    }

    private static TransferSessionRecord Session(string id) => new()
    {
        Id = id,
        DeviceName = "Test iPhone",
        DestinationRoot = @"D:\Photos",
        StartedAt = Now,
        Status = TransferSessionStatus.Running,
    };

    private static TransferRecord Transfer(string session, string name) => new()
    {
        SessionId = session,
        SourceAssetId = "o1",
        SourcePersistentId = "pid-1",
        SourceFileName = name,
        ReportedSize = 4_000,
        PartialPath = @"D:\Photos\IMG_1.HEIC.1234abcd.partial",
        StartedAt = Now,
        Status = TransferStatus.InProgress,
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
