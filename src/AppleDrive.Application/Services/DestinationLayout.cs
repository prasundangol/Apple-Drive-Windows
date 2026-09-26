using System.Globalization;
using AppleDrive.Domain.Enums;

namespace AppleDrive.Application.Services;

/// <summary>Where imported files go: folder organization by date and safe file names.</summary>
public static class DestinationLayout
{
    /// <summary>
    /// The folder for a file taken at <paramref name="captureDate"/> (wall-clock time where it was
    /// taken). Month folder names are always English (<c>09 September</c>) so the library layout
    /// does not change with the Windows display language.
    /// </summary>
    /// <param name="dayKnown">False when only the month is known; the file then goes in the month folder rather than an invented day.</param>
    public static string GetFolder(string root, FolderOrganization organization, DateTime captureDate, bool dayKnown = true)
    {
        var year = captureDate.Year.ToString("D4", CultureInfo.InvariantCulture);
        var month = captureDate.ToString("MM MMMM", CultureInfo.InvariantCulture);
        return organization switch
        {
            FolderOrganization.YearMonth => Path.Combine(root, year, month),
            FolderOrganization.YearMonthDay when !dayKnown => Path.Combine(root, year, month),
            FolderOrganization.YearMonthDay => Path.Combine(root, year, month, captureDate.Day.ToString("D2", CultureInfo.InvariantCulture)),
            _ => root,
        };
    }

    /// <summary>
    /// The month encoded in an iPhone camera-roll folder name (<c>202409__</c>, <c>202409_a</c>),
    /// which is when the item was added to the library. A last-resort date for files that carry none.
    /// </summary>
    public static DateTime? MonthFromPhoneFolder(string sourceFolder)
    {
        var name = sourceFolder[(sourceFolder.LastIndexOf('/') + 1)..];
        if (name.Length != 8
            || name.AsSpan(0, 6).ContainsAnyExceptInRange('0', '9')
            || name[6] != '_'
            || !(name[7] == '_' || char.IsAsciiLetterLower(name[7])))
        {
            return null;
        }

        var year = int.Parse(name.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var month = int.Parse(name.AsSpan(4, 2), CultureInfo.InvariantCulture);
        return year is >= 2000 and <= 2100 && month is >= 1 and <= 12 ? new DateTime(year, month, 1) : null;
    }

    /// <summary>
    /// A file name that is safe on Windows: device-supplied names are reduced to a plain name,
    /// invalid characters are replaced and reserved device names are avoided.
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var name = fileName.Replace('/', '_').Replace('\\', '_');
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim().TrimEnd('.');

        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0)
        {
            name = "IMG" + Path.GetExtension(name);
        }
        else if (IsReservedDeviceName(stem))
        {
            name = "_" + name;
        }

        return name;
    }

    /// <summary><c>IMG_1234</c>, <c>IMG_1234 (1)</c>, <c>IMG_1234 (2)</c>, …</summary>
    public static string WithCounter(string baseName, int counter) =>
        counter == 0 ? baseName : string.Create(CultureInfo.InvariantCulture, $"{baseName} ({counter})");

    private static bool IsReservedDeviceName(string stem) =>
        stem.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL"
            or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9"
            or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";
}

/// <summary>
/// Chooses destination names that are free on disk and not already promised to another transfer
/// in this process, so simultaneous transfers can never pick the same name. Existing files are
/// never overwritten: a clash gets the next <c> (n)</c> suffix.
/// </summary>
public sealed class DestinationNameReservations
{
    private readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Reserves one base name for a set of extensions in <paramref name="folder"/>, so the parts of
    /// a Live Photo keep the same name (<c>IMG_1 (1).HEIC</c> with <c>IMG_1 (1).MOV</c>).
    /// Returns the full paths, in the order of <paramref name="extensions"/>.
    /// </summary>
    public IReadOnlyList<string> Reserve(string folder, string baseName, IReadOnlyList<string> extensions, int startCounter = 0)
    {
        lock (_lock)
        {
            for (var counter = startCounter; ; counter++)
            {
                var name = DestinationLayout.WithCounter(baseName, counter);
                var paths = extensions.Select(extension => Path.Combine(folder, name + extension)).ToList();
                if (paths.All(path => !_reserved.Contains(path) && !File.Exists(path) && !Directory.Exists(path)))
                {
                    foreach (var path in paths)
                    {
                        _reserved.Add(path);
                    }

                    return paths;
                }
            }
        }
    }

    /// <summary>Releases a reservation once its file exists on disk, or was abandoned.</summary>
    public void Release(string path)
    {
        lock (_lock)
        {
            _reserved.Remove(path);
        }
    }

    public bool IsReserved(string path)
    {
        lock (_lock)
        {
            return _reserved.Contains(path);
        }
    }
}
