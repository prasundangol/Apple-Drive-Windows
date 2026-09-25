using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;

namespace AppleDrive.Testing;

/// <summary>Dictionary-backed <see cref="IMediaRepository"/> with the same path semantics as SQLite.</summary>
public sealed class InMemoryMediaRepository : IMediaRepository
{
    private readonly Dictionary<string, IndexedMediaFile> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private long _nextId;

    public IReadOnlyCollection<IndexedMediaFile> All => _byPath.Values;

    public Task<IReadOnlyList<IndexedMediaFile>> GetUnderRootAsync(string root, CancellationToken cancellationToken)
    {
        var prefix = root.EndsWith('\\') ? root : root + '\\';
        return Result(_byPath.Values.Where(file => file.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));
    }

    public Task<IndexedMediaFile?> GetByPathAsync(string fullPath, CancellationToken cancellationToken) =>
        Task.FromResult(_byPath.GetValueOrDefault(fullPath));

    public Task<IReadOnlyList<IndexedMediaFile>> GetAvailableBySizeAsync(long fileSize, CancellationToken cancellationToken) =>
        Result(_byPath.Values.Where(file => file.IsAvailable && file.FileSize == fileSize));

    public Task<IReadOnlyList<IndexedMediaFile>> GetAvailableBySha256Async(byte[] sha256, CancellationToken cancellationToken) =>
        Result(_byPath.Values.Where(file => file.IsAvailable && file.Sha256 is { } hash && hash.AsSpan().SequenceEqual(sha256)));

    public Task UpsertAsync(IReadOnlyCollection<IndexedMediaFile> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var file in files)
        {
            if (_byPath.TryGetValue(file.FullPath, out var existing))
            {
                _byPath[file.FullPath] = file with
                {
                    Id = existing.Id,
                    FirstSeenAt = existing.FirstSeenAt < file.FirstSeenAt ? existing.FirstSeenAt : file.FirstSeenAt,
                };
            }
            else
            {
                _byPath[file.FullPath] = file with { Id = ++_nextId };
            }
        }

        return Task.CompletedTask;
    }

    public Task SetSha256Async(long id, byte[] sha256, CancellationToken cancellationToken) =>
        Update(id, file => file with { Sha256 = sha256 });

    public Task SetPerceptualHashAsync(long id, ulong perceptualHash, CancellationToken cancellationToken) =>
        Update(id, file => file with { PerceptualHash = perceptualHash });

    public Task MarkUnavailableAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken)
    {
        foreach (var id in ids)
        {
            Update(id, file => file with { IsAvailable = false });
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var match = _byPath.Values.FirstOrDefault(file => file.Id == id);
        if (match is not null)
        {
            _byPath.Remove(match.FullPath);
        }

        return Task.CompletedTask;
    }

    public Task<int> DeleteUnavailableBySha256Async(byte[] sha256, CancellationToken cancellationToken)
    {
        var matches = _byPath.Values
            .Where(file => !file.IsAvailable && file.Sha256 is { } hash && hash.AsSpan().SequenceEqual(sha256))
            .ToList();
        foreach (var match in matches)
        {
            _byPath.Remove(match.FullPath);
        }

        return Task.FromResult(matches.Count);
    }

    public Task TouchScannedAsync(string root, DateTimeOffset scannedAt, CancellationToken cancellationToken) => Task.CompletedTask;

    private Task Update(long id, Func<IndexedMediaFile, IndexedMediaFile> change)
    {
        var match = _byPath.Values.FirstOrDefault(file => file.Id == id);
        if (match is not null)
        {
            _byPath[match.FullPath] = change(match);
        }

        return Task.CompletedTask;
    }

    private static Task<IReadOnlyList<IndexedMediaFile>> Result(IEnumerable<IndexedMediaFile> files) =>
        Task.FromResult<IReadOnlyList<IndexedMediaFile>>(files.ToList());
}
