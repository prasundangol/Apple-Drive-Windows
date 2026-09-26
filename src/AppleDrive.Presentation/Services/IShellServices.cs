namespace AppleDrive.Presentation.Services;

/// <summary>Operating-system interactions that ViewModels request but must not perform themselves.</summary>
public interface IShellServices
{
    /// <summary>Shows a folder picker. Returns the chosen path, or <c>null</c> if cancelled.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>Opens a folder in File Explorer.</summary>
    Task OpenFolderAsync(string path);

    /// <summary>Asks the user to confirm an action.</summary>
    Task<ConfirmationResult> ConfirmAsync(ConfirmationRequest request);
}

/// <summary>A confirmation dialog: a title, labelled values, an optional note and check box, and two buttons.</summary>
/// <param name="OptionLabel">When set, a check box with this label is shown.</param>
/// <param name="OptionChecked">Initial state of the check box.</param>
public sealed record ConfirmationRequest(
    string Title,
    IReadOnlyList<KeyValuePair<string, string>> Details,
    string? Footnote,
    string PrimaryButton,
    string CloseButton,
    string? OptionLabel = null,
    bool OptionChecked = false);

/// <summary>What the user chose: <see cref="Confirmed"/> only for the primary button.</summary>
public sealed record ConfirmationResult(bool Confirmed, bool OptionChecked);
