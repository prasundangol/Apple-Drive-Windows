namespace AppleDrive.Application.Interfaces;

/// <summary>SHA-256 digest of a stream and the number of bytes it contained.</summary>
public sealed record HashResult(byte[] Sha256, long Length)
{
    public bool Matches(byte[]? other) => other is not null && Sha256.AsSpan().SequenceEqual(other);
}

/// <summary>Computes SHA-256 content hashes by streaming; never loads a whole file into memory.</summary>
public interface IHashService
{
    /// <summary>Hashes <paramref name="stream"/> from its current position to the end.</summary>
    /// <param name="bytesRead">Receives the running byte count, for progress.</param>
    Task<HashResult> ComputeAsync(Stream stream, IProgress<long>? bytesRead, CancellationToken cancellationToken);

    /// <summary>Hashes a file on disk.</summary>
    Task<HashResult> ComputeFileAsync(string path, CancellationToken cancellationToken);
}
