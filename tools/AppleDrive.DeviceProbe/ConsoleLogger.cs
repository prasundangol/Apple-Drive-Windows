using Microsoft.Extensions.Logging;

namespace AppleDrive.Tools.DeviceProbe;

/// <summary>Minimal console logger so the probe needs no logging framework.</summary>
internal sealed class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        Console.WriteLine($"  [{logLevel}] {formatter(state, exception)}");
        if (exception is not null)
        {
            Console.WriteLine($"  {exception.GetType().Name}: {exception.Message}");
        }
    }
}
