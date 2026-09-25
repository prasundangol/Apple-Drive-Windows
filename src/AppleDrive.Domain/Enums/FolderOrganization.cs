namespace AppleDrive.Domain.Enums;

/// <summary>How imported files are arranged under the destination folder.</summary>
public enum FolderOrganization
{
    /// <summary>All files directly in the destination folder.</summary>
    Flat = 0,

    /// <summary><c>2026\09 September\IMG_1234.HEIC</c></summary>
    YearMonth = 1,

    /// <summary><c>2026\09 September\25\IMG_1234.HEIC</c></summary>
    YearMonthDay = 2,
}
