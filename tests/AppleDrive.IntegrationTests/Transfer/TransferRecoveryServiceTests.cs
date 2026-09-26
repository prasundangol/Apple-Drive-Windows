using System.Security.Cryptography;
using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Infrastructure.Database;
using AppleDrive.Infrastructure.FileSystem;
using AppleDrive.Infrastructure.Hashing;
using AppleDrive.Infrastructure.Metadata;
using AppleDrive.IntegrationTests.Database;
using AppleDrive.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AppleDrive.IntegrationTests.Transfer;

public sealed class TransferRecoveryServiceTests : IAsyncLifetime
{
    private readonly TemporaryDirectory _destination = new();
    private TestDatabase _db = null!;
    private TransferRepository _history = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        _history = new TransferRepository(_db.Database);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        _destination.Dispose();
    }

    [Fact]
    public async Task Crash_while_copying_removes_the_partial_file_and_closes_the_session()
    {
        await SessionAsync("s1");
        var partial = WriteFile("IMG_1.HEIC.0a1b2c3d.partial", Bytes(1_000));
        var id = await TransferAsync("s1", "IMG_1.HEIC", partial);

        var report = await Recovery().RecoverAsync(Ct);

        Assert.False(File.Exists(partial));
        Assert.Equal(new RecoveryReport(1, 0, 1, 1, 0), report);
        Assert.Equal(TransferStatus.Interrupted, (await RecordAsync(id)).Status);
        var session = await _history.GetSessionAsync("s1", Ct);
        Assert.Equal(TransferSessionStatus.Interrupted, session!.Status);
        Assert.NotNull(session.CompletedAt);
        Assert.Empty(await _history.GetRunningSessionsAsync(Ct));
    }

    [Fact]
    public async Task Crash_after_the_rename_keeps_and_records_the_file()
    {
        await SessionAsync("s1");
        var content = MediaFixtures.Jpeg(MediaFixtures.Exif("2024:03:15 09:30:00", "+05:45"));
        var final = WriteFile(@"2024\03 March\IMG_1.JPG", content);
        var id = await TransferAsync("s1", "IMG_1.JPG", final + ".0a1b2c3d.partial");
        await _history.SetTargetAsync(id, final, SHA256.HashData(content), content.Length, Ct);

        var report = await Recovery().RecoverAsync(Ct);

        Assert.Equal(1, report.Completed);
        Assert.Equal(content, File.ReadAllBytes(final));
        var record = await RecordAsync(id);
        Assert.Equal(TransferStatus.Completed, record.Status);
        var indexed = await _db.Repository.GetByPathAsync(final, Ct);
        Assert.Equal(SHA256.HashData(content), indexed!.Sha256);
        Assert.NotNull(indexed.CaptureDate);
        var session = await _history.GetSessionAsync("s1", Ct);
        Assert.Equal(1, session!.TransferredCount);
        Assert.Equal(content.Length, session.TransferredBytes);
    }

    [Fact]
    public async Task A_file_with_different_content_at_the_target_is_left_alone()
    {
        await SessionAsync("s1");
        var someoneElses = Bytes(2_000);
        var final = WriteFile("IMG_1.JPG", someoneElses);
        var id = await TransferAsync("s1", "IMG_1.JPG", final + ".0a1b2c3d.partial");
        await _history.SetTargetAsync(id, final, SHA256.HashData(Bytes(2_000)), 2_000, Ct);

        var report = await Recovery().RecoverAsync(Ct);

        Assert.Equal(0, report.Completed);
        Assert.Equal(someoneElses, File.ReadAllBytes(final));
        Assert.Equal(TransferStatus.Interrupted, (await RecordAsync(id)).Status);
        Assert.Null(await _db.Repository.GetByPathAsync(final, Ct));
    }

    [Fact]
    public async Task Only_the_engines_own_temporary_files_are_ever_deleted()
    {
        await SessionAsync("s1");
        var photo = WriteFile("IMG_1.JPG", Bytes(1_000));
        var lookalike = WriteFile("notes.partial", Bytes(10));
        await TransferAsync("s1", "IMG_1.JPG", photo);
        await TransferAsync("s1", "notes", lookalike);

        var report = await Recovery().RecoverAsync(Ct);

        Assert.True(File.Exists(photo));
        Assert.True(File.Exists(lookalike));
        Assert.Equal(0, report.TemporaryFilesRemoved);
        Assert.Equal(2, report.Interrupted);
    }

    [Fact]
    public async Task Unplugged_destination_defers_recovery_to_the_next_start()
    {
        using var external = new TemporaryDirectory();
        var root = external.Combine("Photos");
        await SessionAsync("s1", root);
        var id = await TransferAsync("s1", "IMG_1.JPG", Path.Combine(root, "IMG_1.JPG.0a1b2c3d.partial"));

        var report = await Recovery().RecoverAsync(Ct);

        Assert.Equal(new RecoveryReport(0, 0, 0, 0, 1), report);
        Assert.Equal(TransferStatus.InProgress, (await RecordAsync(id)).Status);
        Assert.Single(await _history.GetRunningSessionsAsync(Ct));
    }

    [Fact]
    public async Task Finished_sessions_are_not_touched()
    {
        await SessionAsync("s1");
        var id = await TransferAsync("s1", "IMG_1.JPG", _destination.Combine("IMG_1.JPG.0a1b2c3d.partial"));
        await _history.CompleteAsync(id, new TransferOutcomeRecord(TransferStatus.Failed, DateTimeOffset.UtcNow, ErrorKind: "DeviceIo"), Ct);
        await _history.CompleteSessionAsync(Session("s1") with { Status = TransferSessionStatus.Completed, FailedCount = 1 }, Ct);

        var report = await Recovery().RecoverAsync(Ct);

        Assert.Equal(new RecoveryReport(0, 0, 0, 0, 0), report);
        Assert.Equal(TransferStatus.Failed, (await RecordAsync(id)).Status);
    }

    [Fact]
    public async Task Index_failure_after_the_rename_is_recovered_at_the_next_start()
    {
        // The engine renames the file into place, then cannot record it; the run stops. At the
        // next start, recovery finds the verified file through the recorded target.
        var phone = new FakeIPhonePhotoSource();
        await phone.ConnectAsync(FakePhoneDeviceService.TestPhone, CancellationToken.None);
        var content = Bytes(50_000);
        phone.AddFile("Internal Storage/a/IMG_1.JPG", content);
        var lookup = new DestinationContentLookup(_db.Repository, new Sha256HashService(), NullLogger<DestinationContentLookup>.Instance);
        await new DestinationIndexService(new DestinationScanner(NullLogger<DestinationScanner>.Instance), _db.Repository, TimeProvider.System, NullLogger<DestinationIndexService>.Instance)
            .SyncAsync(_destination.Path, null, Ct);
        var assets = await phone.EnumerateAssetsAsync(null, Ct);
        var plan = await new ExactDuplicateDetector(phone, lookup, _history, new Sha256HashService(), NullLogger<ExactDuplicateDetector>.Instance)
            .ClassifyAsync(LivePhotoGrouper.Group(assets.Value), _destination.Path, null, Ct);
        var failing = new FailsToRecordCompletion(_history);
        var service = new MediaTransferService(
            phone, new Sha256HashService(), new CaptureDateReader(), lookup, _db.Repository, failing,
            new DestinationNameReservations(), TimeProvider.System, NullLogger<MediaTransferService>.Instance);

        var run = await service.TransferAsync(new TransferRequest(plan.Value.Items, _destination.Path, FolderOrganization.Flat), null, Ct);

        Assert.Equal(TransferSessionStatus.Stopped, run.Value.Status);
        var record = Assert.Single(await _history.GetInProgressAsync(Ct));

        var report = await Recovery().RecoverAsync(Ct);

        Assert.Equal(1, report.Completed);
        Assert.Equal(TransferStatus.Completed, (await RecordAsync(record.Id, record.SessionId)).Status);
        Assert.Equal(content, File.ReadAllBytes(_destination.Combine("IMG_1.JPG")));
        Assert.Empty(await _history.GetInProgressAsync(Ct));
    }

    private TransferRecoveryService Recovery() => new(
        _history, _db.Repository, new Sha256HashService(), new CaptureDateReader(), TimeProvider.System, NullLogger<TransferRecoveryService>.Instance);

    private TransferSessionRecord Session(string id, string? root = null) => new()
    {
        Id = id,
        DestinationRoot = root ?? _destination.Path,
        StartedAt = DateTimeOffset.UtcNow,
        Status = TransferSessionStatus.Running,
    };

    private Task SessionAsync(string id, string? root = null) => _history.CreateSessionAsync(Session(id, root), Ct);

    private Task<long> TransferAsync(string session, string name, string partialPath) => _history.AddAsync(
        new TransferRecord
        {
            SessionId = session,
            SourceAssetId = "o1",
            SourceFileName = name,
            PartialPath = partialPath,
            StartedAt = DateTimeOffset.UtcNow,
            Status = TransferStatus.InProgress,
        },
        Ct);

    private async Task<TransferRecord> RecordAsync(long id, string session = "s1") =>
        (await _history.GetBySessionAsync(session, Ct)).Single(record => record.Id == id);

    private string WriteFile(string relativePath, byte[] content)
    {
        var path = _destination.Combine(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bytes(int length) => RandomNumberGenerator.GetBytes(length);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Loses the "completed" write, as if the database failed right after a rename.</summary>
    private sealed class FailsToRecordCompletion(ITransferRepository inner) : ITransferRepository
    {
        public Task CreateSessionAsync(TransferSessionRecord session, CancellationToken cancellationToken) => inner.CreateSessionAsync(session, cancellationToken);

        public Task CompleteSessionAsync(TransferSessionRecord session, CancellationToken cancellationToken) => inner.CompleteSessionAsync(session, cancellationToken);

        public Task<TransferSessionRecord?> GetSessionAsync(string sessionId, CancellationToken cancellationToken) => inner.GetSessionAsync(sessionId, cancellationToken);

        public Task<long> AddAsync(TransferRecord record, CancellationToken cancellationToken) => inner.AddAsync(record, cancellationToken);

        public Task CompleteAsync(long id, TransferOutcomeRecord outcome, CancellationToken cancellationToken) =>
            outcome.Status == TransferStatus.Completed
                ? throw new DatabaseException("Simulated failure", new InvalidOperationException())
                : inner.CompleteAsync(id, outcome, cancellationToken);

        public Task SetTargetAsync(long id, string destinationPath, byte[] sha256, long fileSize, CancellationToken cancellationToken) =>
            inner.SetTargetAsync(id, destinationPath, sha256, fileSize, cancellationToken);

        public Task<IReadOnlyList<TransferRecord>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken) => inner.GetBySessionAsync(sessionId, cancellationToken);

        public Task<IReadOnlyList<TransferRecord>> GetInProgressAsync(CancellationToken cancellationToken) => inner.GetInProgressAsync(cancellationToken);

        public Task<IReadOnlyList<TransferSessionRecord>> GetRunningSessionsAsync(CancellationToken cancellationToken) => inner.GetRunningSessionsAsync(cancellationToken);

        public Task<IReadOnlyList<TransferRecord>> GetCompletedUnderRootAsync(string root, CancellationToken cancellationToken) =>
            inner.GetCompletedUnderRootAsync(root, cancellationToken);
    }
}
