namespace AppleDrive.Presentation.Services;

/// <summary>Operating-system interactions that ViewModels request but must not perform themselves.</summary>
public interface IShellServices
{
    /// <summary>Shows a folder picker. Returns the chosen path, or <c>null</c> if cancelled.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>Opens a folder in File Explorer.</summary>
    Task OpenFolderAsync(string path);

    /// <summary>Asks the user to confirm an action. Returns true only for the primary button.</summary>
    Task<bool> ConfirmAsync(ConfirmationRequest request);
}

/// <summary>A confirmation dialog: a title, labelled values, an optional note, and two buttons.</summary>
public sealed record ConfirmationRequest(
    string Title,
    IReadOnlyList<KeyValuePair<string, string>> Details,
    string? Footnote,
    string PrimaryButton,
    string CloseButton);
