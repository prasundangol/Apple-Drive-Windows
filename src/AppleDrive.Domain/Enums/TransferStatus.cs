namespace AppleDrive.Domain.Enums;

/// <summary>State of one file's transfer, as recorded in transfer history. Values are persisted.</summary>
public enum TransferStatus
{
    /// <summary>Copying or verifying. A record left in this state means the app stopped mid-transfer.</summary>
    InProgress = 0,

    /// <summary>Copied, verified and renamed into place.</summary>
    Completed = 1,

    Failed = 2,

    /// <summary>Stopped by the user before it was committed; nothing was left in the destination.</summary>
    Cancelled = 3,

    /// <summary>Copied, but the delivered content turned out to already exist, so the copy was discarded.</summary>
    Duplicate = 4,

    /// <summary>The app stopped (crash, power loss) before this transfer finished; closed by recovery at the next start.</summary>
    Interrupted = 5,
}
