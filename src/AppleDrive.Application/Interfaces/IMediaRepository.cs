using AppleDrive.Domain.Entities;

namespace AppleDrive.Application.Interfaces;

/// <summary>Persistence for the index of destination media files.</summary>
public interface IMediaRepository
{
    /// <summary>Every record (available or not) whose path is inside <paramref name="root"/>.</summary>
    Task<IReadOnlyList<IndexedMediaFile>> GetUnderRootAsync(string root, CancellationToken cancellationToken);

    Task<IndexedMediaFile?> GetByPathAsync(string fullPath, CancellationToken cancellationToken);

    /// <summary>Available records with exactly this size: the only possible exact duplicates.</summary>
    Task<IReadOnlyList<IndexedMediaFile>> GetAvailableBySizeAsync(long fileSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<IndexedMediaFile>> GetAvailableBySha256Async(byte[] sha256, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or updates records by path in one transaction. Updating a record whose size or
    /// modification time changed must be done with cleared hashes by the caller.
    /// </summary>
    Task UpsertAsync(IReadOnlyCollection<IndexedMediaFile> files, CancellationToken cancellationToken);

    Task SetSha256Async(long id, byte[] sha256, CancellationToken cancellationToken);

    Task SetPerceptualHashAsync(long id, ulong perceptualHash, CancellationToken cancellationToken);

    /// <summary>Marks records as not found on disk. Records are kept, never deleted, by scanning.</summary>
    Task MarkUnavailableAsync(IReadOnlyCollection<long> ids, CancellationToken cancellationToken);

    /// <summary>Removes a record, used when a file is known to have moved to another indexed path.</summary>
    Task DeleteAsync(long id, CancellationToken cancellationToken);

    /// <summary>Stamps <c>LastScannedAt</c> on every available record under <paramref name="root"/>.</summary>
    Task TouchScannedAsync(string root, DateTimeOffset scannedAt, CancellationToken cancellationToken);
}
