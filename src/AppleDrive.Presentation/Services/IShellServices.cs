namespace AppleDrive.Presentation.Services;

/// <summary>Operating-system interactions that ViewModels request but must not perform themselves.</summary>
public interface IShellServices
{
    /// <summary>Shows a folder picker. Returns the chosen path, or <c>null</c> if cancelled.</summary>
    Task<string?> PickFolderAsync();

    /// <summary>Opens a folder in File Explorer.</summary>
    Task OpenFolderAsync(string path);
}
