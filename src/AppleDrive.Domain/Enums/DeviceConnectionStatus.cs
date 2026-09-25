namespace AppleDrive.Domain.Enums;

/// <summary>Result of attempting to connect to a phone.</summary>
public enum DeviceConnectionStatus
{
    /// <summary>No supported phone is attached.</summary>
    NotFound = 0,

    /// <summary>The phone is open and its media storage is readable.</summary>
    Connected = 1,

    /// <summary>
    /// The phone is attached but exposes no readable storage. On iPhone this happens
    /// while the phone is locked or before "Trust This Computer" has been accepted.
    /// </summary>
    NeedsUnlockOrTrust = 2,

    /// <summary>The phone is attached but could not be opened (driver problem, busy, etc.).</summary>
    Unavailable = 3,
}
