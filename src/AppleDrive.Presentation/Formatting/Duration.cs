using AppleDrive.Presentation.Resources;

namespace AppleDrive.Presentation.Formatting;

/// <summary>Short remaining-time text: <c>1h 5m</c>, <c>12m 31s</c>, <c>45s</c>.</summary>
public static class Duration
{
    public static string Format(TimeSpan value)
    {
        var rounded = TimeSpan.FromSeconds(Math.Max(0, Math.Round(value.TotalSeconds)));
        if (rounded.TotalHours >= 1)
        {
            return Strings.Format(Strings.DurationHoursFormat, (int)rounded.TotalHours, rounded.Minutes);
        }

        return rounded.TotalMinutes >= 1
            ? Strings.Format(Strings.DurationMinutesFormat, rounded.Minutes, rounded.Seconds)
            : Strings.Format(Strings.DurationSecondsFormat, rounded.Seconds);
    }
}
