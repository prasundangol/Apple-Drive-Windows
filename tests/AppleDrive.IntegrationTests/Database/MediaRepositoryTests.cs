using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;

namespace AppleDrive.IntegrationTests.Database;

public sealed class MediaRepositoryTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private TestDatabase _db = null!;

    public async ValueTask InitializeAsync() => _db = await TestDatabase.CreateAsync();

    public ValueTask DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task Inserts_and_reads_back_every_field()
    {
        var sha = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var file = File(@"D:\Photos\IMG_1.HEIC", 4_821_342) with
        {
            CreatedAt = Now.AddDays(-1),
            Width = 4032,
            Height = 3024,
            CaptureDate = Now.AddDays(-2),
            Sha256 = sha,
            PerceptualHash = 0xF0F0_0000_FFFF_1234,
        };

        await _db.Repository.UpsertAsync([file], Ct);
        var stored = await _db.Repository.GetByPathAsync(@"D:\Photos\IMG_1.HEIC", Ct);

        Assert.NotNull(stored);
        Assert.True(stored.Id > 0);
        Assert.Equal(file with { Id = stored.Id }, stored with { Sha256 = file.Sha256 });
        Assert.Equal(sha, stored.Sha256);
        Assert.Equal(0xF0F0_0000_FFFF_1234, stored.PerceptualHash);
    }

    [Fact]
    public async Task Upsert_updates_existing_path_and_keeps_first_seen()
    {
        await _db.Repository.UpsertAsync([File(@"D:\Photos\a.jpg", 100)], Ct);

        await _db.Repository.UpsertAsync([File(@"D:\Photos\a.jpg", 200) with { FirstSeenAt = Now.AddDays(5), LastScannedAt = Now.AddDays(5) }], Ct);

        var stored = await _db.Repository.GetByPathAsync(@"D:\Photos\a.jpg", Ct);
        Assert.Equal(200, stored!.FileSize);
        Assert.Equal(Now, stored.FirstSeenAt);
        Assert.Equal(Now.AddDays(5), stored.LastScannedAt);
    }

    [Fact]
    public async Task Path_lookup_is_case_insensitive()
    {
        await _db.Repository.UpsertAsync([File(@"D:\Photos\IMG_1.JPG", 1)], Ct);

        Assert.NotNull(await _db.Repository.GetByPathAsync(@"d:\photos\img_1.jpg", Ct));
    }

    [Fact]
    public async Task Root_query_excludes_sibling_folders_with_the_same_prefix()
    {
        await _db.Repository.UpsertAsync(
        [
            File(@"D:\Photos\a.jpg", 1),
            File(@"D:\Photos\2024\b.jpg", 1),
            File(@"D:\Photos2\c.jpg", 1),
            File(@"D:\Photos-old\d.jpg", 1),
            File(@"D:\Other\e.jpg", 1),
        ], Ct);

        var under = await _db.Repository.GetUnderRootAsync(@"D:\Photos", Ct);

        Assert.Equal([@"D:\Photos\2024\b.jpg", @"D:\Photos\a.jpg"], under.Select(f => f.FullPath).Order());
    }

    [Fact]
    public async Task Drive_root_includes_everything_on_that_drive()
    {
        await _db.Repository.UpsertAsync([File(@"D:\a.jpg", 1), File(@"D:\x\b.jpg", 1), File(@"E:\c.jpg", 1)], Ct);

        var under = await _db.Repository.GetUnderRootAsync(@"D:\", Ct);

        Assert.Equal(2, under.Count);
    }

    [Fact]
    public async Task Size_and_hash_lookups_return_only_available_files()
    {
        var sha = new byte[32];
        sha[0] = 7;
        await _db.Repository.UpsertAsync(
        [
            File(@"D:\P\a.jpg", 500) with { Sha256 = sha },
            File(@"D:\P\b.jpg", 500),
            File(@"D:\P\c.jpg", 501),
        ], Ct);
        var b = await _db.Repository.GetByPathAsync(@"D:\P\b.jpg", Ct);
        await _db.Repository.MarkUnavailableAsync([b!.Id], Ct);

        var bySize = await _db.Repository.GetAvailableBySizeAsync(500, Ct);
        var bySha = await _db.Repository.GetAvailableBySha256Async(sha, Ct);

        Assert.Equal(@"D:\P\a.jpg", Assert.Single(bySize).FullPath);
        Assert.Equal(@"D:\P\a.jpg", Assert.Single(bySha).FullPath);
    }

    [Fact]
    public async Task Marking_unavailable_keeps_the_record()
    {
        await _db.Repository.UpsertAsync([File(@"D:\P\a.jpg", 1)], Ct);
        var record = await _db.Repository.GetByPathAsync(@"D:\P\a.jpg", Ct);

        await _db.Repository.MarkUnavailableAsync([record!.Id], Ct);

        var stored = await _db.Repository.GetByPathAsync(@"D:\P\a.jpg", Ct);
        Assert.NotNull(stored);
        Assert.False(stored.IsAvailable);
    }

    [Fact]
    public async Task Marks_many_records_unavailable_in_chunks()
    {
        var files = Enumerable.Range(0, 1_250).Select(i => File($@"D:\P\{i}.jpg", i)).ToList();
        await _db.Repository.UpsertAsync(files, Ct);
        var ids = (await _db.Repository.GetUnderRootAsync(@"D:\P", Ct)).Select(f => f.Id).ToList();

        await _db.Repository.MarkUnavailableAsync(ids, Ct);

        Assert.All(await _db.Repository.GetUnderRootAsync(@"D:\P", Ct), f => Assert.False(f.IsAvailable));
    }

    [Fact]
    public async Task Sets_hashes_and_deletes()
    {
        await _db.Repository.UpsertAsync([File(@"D:\P\a.jpg", 1)], Ct);
        var id = (await _db.Repository.GetByPathAsync(@"D:\P\a.jpg", Ct))!.Id;
        var sha = Enumerable.Repeat((byte)0xAB, 32).ToArray();

        await _db.Repository.SetSha256Async(id, sha, Ct);
        await _db.Repository.SetPerceptualHashAsync(id, ulong.MaxValue, Ct);
        var stored = await _db.Repository.GetByPathAsync(@"D:\P\a.jpg", Ct);
        await _db.Repository.DeleteAsync(id, Ct);

        Assert.Equal(sha, stored!.Sha256);
        Assert.Equal(ulong.MaxValue, stored.PerceptualHash);
        Assert.Null(await _db.Repository.GetByPathAsync(@"D:\P\a.jpg", Ct));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IndexedMediaFile File(string path, long size) => new()
    {
        FullPath = path,
        MediaType = MediaType.Image,
        FileSize = size,
        ModifiedAt = Now,
        FirstSeenAt = Now,
        LastScannedAt = Now,
    };
}
