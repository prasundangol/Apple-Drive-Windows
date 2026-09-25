using System.Buffers;
using System.Security.Cryptography;
using AppleDrive.Application.Interfaces;

namespace AppleDrive.Infrastructure.Hashing;

/// <summary>Streaming SHA-256 with pooled 1 MB buffers and asynchronous, sequential file reads.</summary>
public sealed class Sha256HashService : IHashService
{
    private const int BufferSize = 1024 * 1024;

    public async Task<HashResult> ComputeAsync(Stream stream, IProgress<long>? bytesRead, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                total += read;
                bytesRead?.Report(total);
            }

            return new HashResult(hash.GetHashAndReset(), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<HashResult> ComputeFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 0,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ComputeAsync(stream, null, cancellationToken).ConfigureAwait(false);
    }
}
