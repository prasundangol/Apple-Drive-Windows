using AppleDrive.Domain.Enums;

namespace AppleDrive.Application.Settings;

/// <summary>Minimum severity written to the diagnostic log.</summary>
public enum DiagnosticLogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>User preferences persisted between sessions.</summary>
public sealed record AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public bool HasCompletedOnboarding { get; init; }

    /// <summary>Last destination folder chosen by the user, or <c>null</c> before one is chosen.</summary>
    public string? DestinationFolder { get; init; }

    public FolderOrganization Organization { get; init; } = FolderOrganization.YearMonth;

    /// <summary>Byte-for-byte duplicates are skipped automatically (the safe default).</summary>
    public bool SkipExactDuplicates { get; init; } = true;

    /// <summary>Custom thumbnail cache folder, or <c>null</c> to use the default location.</summary>
    public string? ThumbnailCacheFolder { get; init; }

    public DiagnosticLogLevel LogLevel { get; init; } = DiagnosticLogLevel.Information;
}
