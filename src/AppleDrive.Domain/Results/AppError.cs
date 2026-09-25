namespace AppleDrive.Domain.Results;

/// <summary>
/// A structured failure. <see cref="Kind"/> drives the user-facing message;
/// <see cref="Details"/> holds diagnostic text shown only under "Show details".
/// </summary>
public sealed record AppError(ErrorKind Kind, string Details, int? HResult = null)
{
    public static AppError Cancelled { get; } = new(ErrorKind.Cancelled, "The operation was cancelled.");

    public override string ToString() =>
        HResult is { } hr ? $"{Kind} (0x{hr:X8}): {Details}" : $"{Kind}: {Details}";
}
