namespace AppleDrive.Application.Interfaces;

/// <summary>When a photo or video was taken, as recorded in the file itself.</summary>
/// <param name="Value">
/// The capture time. <see cref="DateTimeOffset.DateTime"/> is the wall-clock time where it was
/// taken, which is what folder organization uses.
/// </param>
/// <param name="HasKnownOffset">False when the file stores only a local time and the offset was assumed.</param>
public sealed record CaptureDate(DateTimeOffset Value, bool HasKnownOffset);

/// <summary>Reads the capture date from photo (EXIF) and video (QuickTime) metadata.</summary>
public interface ICaptureDateReader
{
    /// <summary>
    /// Returns the capture date, or <c>null</c> when the file has none or its format is not
    /// recognized. Reads only metadata, never the whole file. Throws only for I/O failures.
    /// </summary>
    Task<CaptureDate?> ReadAsync(string path, CancellationToken cancellationToken);
}
