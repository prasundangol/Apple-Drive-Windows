namespace AppleDrive.Domain.Enums;

/// <summary>Outcome of a whole transfer run. Values are persisted.</summary>
public enum TransferSessionStatus
{
    Running = 0,
    Completed = 1,
    Cancelled = 2,

    /// <summary>Ended early by an error that affects every remaining file (phone unplugged, disk full…).</summary>
    Stopped = 3,
}
