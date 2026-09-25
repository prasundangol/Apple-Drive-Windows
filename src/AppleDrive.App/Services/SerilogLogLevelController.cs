using AppleDrive.Application.Interfaces;
using AppleDrive.Application.Settings;
using Serilog.Core;
using Serilog.Events;

namespace AppleDrive.App.Services;

/// <summary>Maps the user's log-level setting onto a Serilog level switch.</summary>
internal sealed class SerilogLogLevelController : ILogLevelController
{
    public LoggingLevelSwitch Switch { get; } = new(LogEventLevel.Information);

    public void SetLevel(DiagnosticLogLevel level) =>
        Switch.MinimumLevel = level switch
        {
            DiagnosticLogLevel.Debug => LogEventLevel.Debug,
            DiagnosticLogLevel.Warning => LogEventLevel.Warning,
            DiagnosticLogLevel.Error => LogEventLevel.Error,
            _ => LogEventLevel.Information,
        };
}
