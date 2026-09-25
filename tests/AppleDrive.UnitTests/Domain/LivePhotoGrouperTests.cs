using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Media;

namespace AppleDrive.UnitTests.Domain;

public sealed class LivePhotoGrouperTests
{
    [Fact]
    public void Pairs_image_and_mov_with_same_base_name_in_same_folder()
    {
        var items = LivePhotoGrouper.Group([Asset("DCIM/202409__/IMG_1234.HEIC"), Asset("DCIM/202409__/IMG_1234.MOV")]);

        var item = Assert.Single(items);
        Assert.True(item.IsLivePhoto);
        Assert.Equal("IMG_1234.HEIC", item.Primary.FileName);
        Assert.Equal("IMG_1234.MOV", item.MotionVideo!.FileName);
    }

    [Fact]
    public void Matches_base_names_case_insensitively()
    {
        var items = LivePhotoGrouper.Group([Asset("DCIM/a/img_0001.heic"), Asset("DCIM/a/IMG_0001.MOV")]);

        Assert.True(Assert.Single(items).IsLivePhoto);
    }

    [Fact]
    public void Does_not_pair_across_folders()
    {
        // IMG_0001 repeats across DCIM folders on real iPhones.
        var items = LivePhotoGrouper.Group([Asset("DCIM/202409__/IMG_0001.HEIC"), Asset("DCIM/202410__/IMG_0001.MOV")]);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.False(item.IsLivePhoto));
    }

    [Fact]
    public void Does_not_pair_image_with_mp4()
    {
        var items = LivePhotoGrouper.Group([Asset("DCIM/a/IMG_0002.JPG"), Asset("DCIM/a/IMG_0002.MP4")]);

        Assert.All(items, item => Assert.False(item.IsLivePhoto));
    }

    [Fact]
    public void Ambiguous_groups_are_left_unpaired()
    {
        var items = LivePhotoGrouper.Group(
        [
            Asset("DCIM/a/IMG_0003.HEIC"),
            Asset("DCIM/a/IMG_0003.JPG"),
            Asset("DCIM/a/IMG_0003.MOV"),
        ]);

        Assert.Equal(3, items.Count);
        Assert.All(items, item => Assert.False(item.IsLivePhoto));
    }

    [Fact]
    public void Edited_live_photo_is_paired_separately_from_original()
    {
        var items = LivePhotoGrouper.Group(
        [
            Asset("DCIM/a/IMG_0004.HEIC"),
            Asset("DCIM/a/IMG_0004.MOV"),
            Asset("DCIM/a/IMG_E0004.HEIC"),
            Asset("DCIM/a/IMG_E0004.MOV"),
        ]);

        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.True(item.IsLivePhoto));
    }

    [Fact]
    public void Every_asset_appears_exactly_once()
    {
        PhotoAsset[] assets =
        [
            Asset("DCIM/a/IMG_1.HEIC"), Asset("DCIM/a/IMG_1.MOV"),
            Asset("DCIM/a/IMG_2.PNG"), Asset("DCIM/a/IMG_3.MOV"),
            Asset("DCIM/b/IMG_1.HEIC"), Asset("DCIM/b/IMG_4.MP4"),
            Asset("DCIM/b/IMG_5.JPG"), Asset("DCIM/b/IMG_5.HEIC"), Asset("DCIM/b/IMG_5.MOV"),
        ];

        var components = LivePhotoGrouper.Group(assets).SelectMany(item => item.Components).ToList();

        Assert.Equal(assets.Length, components.Count);
        Assert.Equal(assets.OrderBy(a => a.Id), components.OrderBy(a => a.Id));
    }

    private static int _nextId;

    private static PhotoAsset Asset(string path)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        return new PhotoAsset
        {
            Id = $"id{Interlocked.Increment(ref _nextId)}",
            FileName = name,
            SourcePath = path,
            MediaType = MediaFormats.GetMediaType(name),
        };
    }
}
