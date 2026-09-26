using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Enums;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AppleDrive.App.Views;

/// <summary>x:Bind helpers for the review grid.</summary>
public static class Previews
{
    /// <summary>A preview from the thumbnail cache, decoded at display size.</summary>
    public static ImageSource? Load(string? path) =>
        path is null ? null : new BitmapImage(new Uri(path)) { DecodePixelWidth = IThumbnailService.Size, DecodePixelType = DecodePixelType.Logical };

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility HasText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Duplicates show the destination file beside the phone item.</summary>
    public static GridLength SecondColumn(string? otherPath) =>
        string.IsNullOrEmpty(otherPath) ? new GridLength(0) : new GridLength(1, GridUnitType.Star);

    public static string PlaceholderGlyph(bool isVideo) => isVideo ? "" : "";

    public static string Facts(string kind, string size, string date) => $"{kind} · {size} · {date}";

    public static string StatusGlyph(AssetStatus status) => status switch
    {
        AssetStatus.ExactDuplicate => "",
        AssetStatus.PossibleDuplicate => "",
        _ => "",
    };
}
