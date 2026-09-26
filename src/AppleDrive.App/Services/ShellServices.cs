using AppleDrive.Presentation.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    public async Task<ConfirmationResult> ConfirmAsync(ConfirmationRequest request)
    {
        var details = new Grid { ColumnSpacing = 24, RowSpacing = 8 };
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        details.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var row = 0; row < request.Details.Count; row++)
        {
            details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock
            {
                Text = request.Details[row].Key,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            var value = new TextBlock
            {
                Text = request.Details[row].Value,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["BodyStrongTextBlockStyle"],
            };
            Grid.SetRow(label, row);
            Grid.SetRow(value, row);
            Grid.SetColumn(value, 1);
            details.Children.Add(label);
            details.Children.Add(value);
        }

        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(details);
        CheckBox? option = null;
        if (request.OptionLabel is { } optionLabel)
        {
            option = new CheckBox { Content = new TextBlock { Text = optionLabel, TextWrapping = TextWrapping.WrapWholeWords }, IsChecked = request.OptionChecked };
            content.Children.Add(option);
        }

        if (request.Footnote is { } footnote)
        {
            content.Children.Add(new TextBlock
            {
                Text = footnote,
                TextWrapping = TextWrapping.WrapWholeWords,
                Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["CaptionTextBlockStyle"],
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = App.MainWindow.Content.XamlRoot,
            Title = request.Title,
            Content = content,
            PrimaryButtonText = request.PrimaryButton,
            CloseButtonText = request.CloseButton,
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = (App.MainWindow.Content as FrameworkElement)?.ActualTheme ?? ElementTheme.Default,
        };
        var confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
        return new ConfirmationResult(confirmed, option?.IsChecked == true);
    }
}
