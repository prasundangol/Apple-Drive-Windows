namespace AppleDrive.Domain.Enums;

/// <summary>Lifecycle state of a source asset during an import session.</summary>
public enum AssetStatus
{
    New = 0,
    ExactDuplicate = 1,
    PossibleDuplicate = 2,
    SelectedForTransfer = 3,
    Skipped = 4,
    Transferred = 5,
    Failed = 6,
}
