using AppleDrive.Domain.Enums;

namespace AppleDrive.Domain.Entities;

/// <summary>
/// A user-visible unit of media: a single photo or video, or a Live Photo made of an
/// image plus its motion video. Components of a Live Photo are always imported together.
/// </summary>
public sealed class MediaItem
{
    public MediaItem(PhotoAsset primary, PhotoAsset? motionVideo = null)
    {
        ArgumentNullException.ThrowIfNull(primary);
        if (motionVideo is not null && primary.MediaType != MediaType.Image)
        {
            throw new ArgumentException("A Live Photo's primary component must be an image.", nameof(primary));
        }

        Primary = primary;
        MotionVideo = motionVideo;
    }

    public PhotoAsset Primary { get; }

    public PhotoAsset? MotionVideo { get; }

    public bool IsLivePhoto => MotionVideo is not null;

    public MediaType MediaType => Primary.MediaType;

    public IEnumerable<PhotoAsset> Components =>
        MotionVideo is null ? [Primary] : [Primary, MotionVideo];
}
