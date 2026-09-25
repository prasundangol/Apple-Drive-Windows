namespace AppleDrive.Application.Interfaces;

/// <summary>A supported media file found on disk.</summary>
public sealed record ScannedFile(string FullPath, long Size, DateTimeOffset CreatedAt, DateTimeOffset ModifiedAt);

/// <summary>Lists supported media files under a folder.</summary>
public interface IDestinationScanner
{
    /// <summary>
    /// Streams every supported media file under <paramref name="root"/>, recursively.
    /// Folders that can't be read are skipped. Throws <see cref="IOException"/> if the
    /// root itself becomes unavailable mid-scan (for example, the drive is unplugged).
    /// </summary>
    IAsyncEnumerable<ScannedFile> EnumerateAsync(string root, CancellationToken cancellationToken);
}
