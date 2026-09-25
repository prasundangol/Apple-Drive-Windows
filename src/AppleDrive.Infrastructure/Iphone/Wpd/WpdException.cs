using AppleDrive.Domain.Results;

namespace AppleDrive.Infrastructure.Iphone.Wpd;

/// <summary>
/// Carries a structured WPD failure out of deep interop code. It never escapes the
/// infrastructure layer: public entry points convert it back to an <see cref="AppError"/>.
/// </summary>
internal sealed class WpdException(AppError error) : Exception(error.ToString())
{
    public AppError Error { get; } = error;
}
