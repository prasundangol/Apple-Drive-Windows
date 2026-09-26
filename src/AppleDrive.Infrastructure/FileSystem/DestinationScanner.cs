using System.IO.Enumeration;
using System.Runtime.CompilerServices;
using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Services;
using AppleDrive.Domain.Media;
using Microsoft.Extensions.Logging;

namespace AppleDrive.Infrastructure.FileSystem;

/// <summary>
/// Recursive scan built on <see cref="FileSystemEnumerable{TResult}"/>, which reads size and
/// timestamps from the directory listing itself, with no extra call per file.
/// </summary>
public sealed class DestinationScanner(ILogger<DestinationScanner> logger) : IDestinationScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        // Reparse points (junctions, symlinks) can create cycles or leave the destination.
        AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline,
        MatchType = MatchType.Simple,
        BufferSize = 64 * 1024,
    };

    public async IAsyncEnumerable<ScannedFile> EnumerateAsync(
        string root,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException("The destination folder is not available.");
        }

        var enumerable = new FileSystemEnumerable<ScannedFile>(
            root,
            static (ref FileSystemEntry entry) => new ScannedFile(
                entry.ToFullPath(),
                entry.Length,
                entry.CreationTimeUtc,
                entry.LastWriteTimeUtc),
            Options)
        {
            ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                !entry.IsDirectory
                && !entry.FileName.EndsWith(MediaTransferService.PartialFileSuffix, StringComparison.OrdinalIgnoreCase)
                && MediaFormats.IsSupported(entry.FileName.ToString()),
        };

        // Directory enumeration is synchronous; run it off the caller's thread in chunks.
        using var enumerator = enumerable.GetEnumerator();
        var chunk = new List<ScannedFile>(256);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            chunk.Clear();
            var more = await Task.Run(
                () =>
                {
                    while (chunk.Count < 256)
                    {
                        if (!enumerator.MoveNext())
                        {
                            return false;
                        }

                        chunk.Add(enumerator.Current);
                    }

                    return true;
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var file in chunk)
            {
                yield return file;
            }

            if (!more)
            {
                break;
            }
        }

        // A root that vanished mid-scan can end enumeration early without an error, so the
        // caller must not treat unseen files as deleted in that case.
        if (!Directory.Exists(root))
        {
            logger.LogWarning("Destination folder disappeared during the scan");
            throw new DirectoryNotFoundException("The destination folder became unavailable during the scan.");
        }
    }
}
