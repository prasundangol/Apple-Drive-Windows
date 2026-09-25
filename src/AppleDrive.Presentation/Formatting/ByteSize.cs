using System.Globalization;

namespace AppleDrive.Presentation.Formatting;

/// <summary>Human-readable byte counts (decimal units, as File Explorer uses for drives).</summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        if (bytes < 1000)
        {
            return string.Create(CultureInfo.CurrentCulture, $"{bytes} B");
        }

        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {Units[unit]}");
    }
}
