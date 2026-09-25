using AppleDrive.Presentation.Services;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace AppleDrive.App.Services;

internal sealed class ShellServices : IShellServices
{
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
        };
        picker.FileTypeFilter.Add("*");

        // Unpackaged desktop apps must associate the picker with their window.
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    public async Task OpenFolderAsync(string path)
    {
        if (!await Launcher.LaunchFolderPathAsync(path))
        {
            Serilog.Log.Warning("Could not open folder {Path}", path);
        }
    }
}
