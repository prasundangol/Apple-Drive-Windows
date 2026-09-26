using AppleDrive.Application.Interfaces;
using AppleDrive.Domain.Entities;

namespace AppleDrive.Testing;

/// <summary>Returns made-up preview paths and records what was asked for.</summary>
public sealed class FakeThumbnailService : IThumbnailService
{
    public List<string> PhoneRequests { get; } = [];

    public List<string> FileRequests { get; } = [];

    public Task<string?> GetPhoneThumbnailAsync(PhotoAsset asset, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PhoneRequests.Add(asset.FileName);
        return Task.FromResult<string?>(@"C:\previews\" + asset.FileName + ".jpg");
    }

    public Task<string?> GetFileThumbnailAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileRequests.Add(path);
        return Task.FromResult<string?>(path + ".preview.jpg");
    }
}
