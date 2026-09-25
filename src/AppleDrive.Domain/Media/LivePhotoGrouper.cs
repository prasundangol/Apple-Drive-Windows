using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;

namespace AppleDrive.Domain.Media;

/// <summary>
/// Pairs iPhone Live Photo components. Over USB an iPhone exposes a Live Photo as two sibling
/// files in the same folder with the same base name, e.g. <c>IMG_1234.HEIC</c> and <c>IMG_1234.MOV</c>.
/// </summary>
public static class LivePhotoGrouper
{
    /// <summary>Groups assets into media items, keeping every input asset exactly once.</summary>
    public static IReadOnlyList<MediaItem> Group(IEnumerable<PhotoAsset> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var items = new List<MediaItem>();
        var groups = assets.GroupBy(
            asset => (asset.SourceFolder, BaseName: asset.BaseName.ToUpperInvariant()));

        foreach (var group in groups)
        {
            var members = group.ToList();
            var images = members.Where(asset => asset.MediaType == MediaType.Image).ToList();
            var videos = members.Where(asset => asset.MediaType == MediaType.Video).ToList();

            // Only an unambiguous single image + single QuickTime video is treated as a Live Photo.
            if (images.Count == 1 && videos.Count == 1 && videos[0].Extension == ".mov")
            {
                items.Add(new MediaItem(images[0], videos[0]));
                items.AddRange(members
                    .Where(asset => asset != images[0] && asset != videos[0])
                    .Select(asset => new MediaItem(asset)));
                continue;
            }

            items.AddRange(members.Select(asset => new MediaItem(asset)));
        }

        return items;
    }
}
