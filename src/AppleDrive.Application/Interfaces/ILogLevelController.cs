using AppleDrive.Application.Settings;

namespace AppleDrive.Application.Interfaces;

/// <summary>Changes the diagnostic log level at runtime.</summary>
public interface ILogLevelController
{
    void SetLevel(DiagnosticLogLevel level);
}
