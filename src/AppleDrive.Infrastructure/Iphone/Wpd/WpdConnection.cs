using AppleDrive.Domain.Entities;
using AppleDrive.Domain.Enums;
using AppleDrive.Domain.Media;
using AppleDrive.Infrastructure.Iphone.Wpd.Interop;

namespace AppleDrive.Infrastructure.Iphone.Wpd;

/// <summary>
/// An open, read-only WPD session with one device. All COM calls are serialized through a
/// single lock; callers must run on thread-pool (MTA) threads, never on the UI thread.
/// Failures surface as <see cref="WpdException"/>.
/// </summary>
internal sealed class WpdConnection : IDisposable
{
    private const int EnumerationBatchSize = 64;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _streamSlot = new(1, 1);
    private readonly IPortableDevice _device;
    private readonly IPortableDeviceContent _content;
    private readonly IPortableDeviceProperties _properties;
    private readonly IPortableDeviceResources _resources;
    private readonly IPortableDeviceKeyCollection _objectKeys;
    private bool _disposed;

    private WpdConnection(
        DeviceInfo device,
        IPortableDevice portableDevice,
        IPortableDeviceContent content,
        IPortableDeviceProperties properties,
        IPortableDeviceResources resources,
        IPortableDeviceKeyCollection objectKeys)
    {
        Device = device;
        _device = portableDevice;
        _content = content;
        _properties = properties;
        _resources = resources;
        _objectKeys = objectKeys;
    }

    public DeviceInfo Device { get; }

    /// <summary>Opens <paramref name="device"/> for reading.</summary>
    public static WpdConnection Open(DeviceInfo device)
    {
        var clientInfo = ComFactory.Create<IPortableDeviceValues>(WpdConstants.ClsidPortableDeviceValues);
        var portableDevice = ComFactory.Create<IPortableDevice>(WpdConstants.ClsidPortableDeviceFtm);
        try
        {
            var version = typeof(WpdConnection).Assembly.GetName().Version ?? new Version(0, 1, 0);
            Check(clientInfo.SetStringValue(WpdKeys.ClientName, "Apple Drive"), "Set client info");
            Check(clientInfo.SetUnsignedIntegerValue(WpdKeys.ClientMajorVersion, (uint)version.Major), "Set client info");
            Check(clientInfo.SetUnsignedIntegerValue(WpdKeys.ClientMinorVersion, (uint)version.Minor), "Set client info");
            Check(clientInfo.SetUnsignedIntegerValue(WpdKeys.ClientRevision, (uint)Math.Max(version.Build, 0)), "Set client info");
            Check(clientInfo.SetUnsignedIntegerValue(WpdKeys.ClientSecurityQualityOfService, WpdConstants.SecurityImpersonation), "Set client info");
            Check(clientInfo.SetUnsignedIntegerValue(WpdKeys.ClientDesiredAccess, WpdConstants.GenericRead), "Set client info");

            Check(portableDevice.Open(device.Id, clientInfo), "Opening the device");
            Check(portableDevice.Content(out var content), "Opening device content");
            Check(content.Properties(out var properties), "Opening device properties");
            Check(content.Transfer(out var resources), "Opening device resources");

            var keys = ComFactory.Create<IPortableDeviceKeyCollection>(WpdConstants.ClsidPortableDeviceKeyCollection);
            foreach (var key in ObjectKeys)
            {
                Check(keys.Add(key), "Preparing property keys");
            }

            return new WpdConnection(device, portableDevice, content, properties, resources, keys);
        }
        catch
        {
            portableDevice.Close();
            ComFactory.Release(portableDevice);
            throw;
        }
        finally
        {
            ComFactory.Release(clientInfo);
        }
    }

    private static readonly PropertyKey[] ObjectKeys =
    [
        WpdKeys.ObjectName,
        WpdKeys.ObjectOriginalFileName,
        WpdKeys.ObjectContentType,
        WpdKeys.ObjectSize,
        WpdKeys.ObjectDateCreated,
        WpdKeys.ObjectDateModified,
        WpdKeys.ObjectDateAuthored,
        WpdKeys.ObjectPersistentUniqueId,
        WpdKeys.MediaWidth,
        WpdKeys.MediaHeight,
    ];

    /// <summary>
    /// True when at least one storage exposes content. A locked or untrusted iPhone opens
    /// successfully but exposes empty (or no) storage.
    /// </summary>
    public bool HasReadableContent()
    {
        foreach (var storageId in EnumerateChildIds(WpdConstants.DeviceObjectId, CancellationToken.None))
        {
            if (EnumerateChildIds(storageId, CancellationToken.None).Any())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Recursively lists every media file on the device.</summary>
    /// <param name="onAssetFound">Called with the running count after each media file is found.</param>
    /// <param name="onObjectSkipped">Called when an object's properties could not be read.</param>
    public List<PhotoAsset> EnumerateMedia(
        Action<int>? onAssetFound,
        Action<string, int>? onObjectSkipped,
        CancellationToken cancellationToken)
    {
        var assets = new List<PhotoAsset>();
        var pending = new Stack<(string ObjectId, string Path)>();
        pending.Push((WpdConstants.DeviceObjectId, string.Empty));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (parentId, parentPath) = pending.Pop();

            foreach (var objectId in EnumerateChildIds(parentId, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryReadObject(objectId, out var info, out var hr))
                {
                    onObjectSkipped?.Invoke(parentPath, hr);
                    continue;
                }

                var path = parentPath.Length == 0 ? info.DisplayName : $"{parentPath}/{info.DisplayName}";
                if (info.ContentType == WpdConstants.ContentTypeFolder
                    || info.ContentType == WpdConstants.ContentTypeFunctionalObject)
                {
                    pending.Push((objectId, path));
                    continue;
                }

                var mediaType = ClassifyMedia(info);
                if (mediaType == MediaType.Unknown)
                {
                    continue;
                }

                assets.Add(new PhotoAsset
                {
                    Id = objectId,
                    PersistentId = info.PersistentId,
                    FileName = info.FileName,
                    SourcePath = path,
                    MediaType = mediaType,
                    ReportedSize = info.Size,
                    CreatedAt = info.DateAuthored ?? info.DateCreated,
                    ModifiedAt = info.DateModified,
                    Width = info.Width,
                    Height = info.Height,
                    MimeType = MediaFormats.GetMimeType(info.FileName),
                });
                onAssetFound?.Invoke(assets.Count);
            }
        }

        return assets;
    }

    /// <summary>
    /// Opens the default data resource of an object. The device supports one open stream at a
    /// time, so this waits until any previously opened stream has been disposed.
    /// </summary>
    public WpdReadStream OpenRead(string objectId, CancellationToken cancellationToken) =>
        OpenRead(objectId, WpdKeys.ResourceDefaultKey, cancellationToken);

    /// <summary>Opens a resource of an object, such as its thumbnail. Same one-stream rule as <see cref="OpenRead(string, CancellationToken)"/>.</summary>
    public WpdReadStream OpenRead(string objectId, PropertyKey resource, CancellationToken cancellationToken)
    {
        _streamSlot.Wait(cancellationToken);
        var slotHandedOff = false;
        try
        {
            uint optimalBufferSize = 256 * 1024;
            nint streamPointer;
            _lock.Wait(cancellationToken);
            try
            {
                ThrowIfDisposed();
                Check(
                    _resources.GetStream(objectId, resource, WpdConstants.StgmRead, ref optimalBufferSize, out streamPointer),
                    "Opening a file on the device");
            }
            finally
            {
                _lock.Release();
            }

            var stream = new WpdReadStream(
                new NativeComStream(streamPointer),
                _lock,
                (int)Math.Clamp(optimalBufferSize, 64 * 1024, 4 * 1024 * 1024),
                () => _streamSlot.Release());
            slotHandedOff = true;
            return stream;
        }
        finally
        {
            if (!slotHandedOff)
            {
                _streamSlot.Release();
            }
        }
    }

    public void Dispose()
    {
        _lock.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _device.Close();
            ComFactory.Release(_objectKeys);
            ComFactory.Release(_resources);
            ComFactory.Release(_properties);
            ComFactory.Release(_content);
            ComFactory.Release(_device);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static MediaType ClassifyMedia(ObjectInfo info)
    {
        // The extension is authoritative when known; content type covers files without one.
        var byExtension = MediaFormats.GetMediaType(info.FileName);
        if (byExtension != MediaType.Unknown)
        {
            return byExtension;
        }

        if (info.ContentType == WpdConstants.ContentTypeImage)
        {
            return MediaType.Image;
        }

        return info.ContentType == WpdConstants.ContentTypeVideo ? MediaType.Video : MediaType.Unknown;
    }

    private unsafe IEnumerable<string> EnumerateChildIds(string parentId, CancellationToken cancellationToken)
    {
        IEnumPortableDeviceObjectIDs enumerator;
        _lock.Wait(cancellationToken);
        try
        {
            ThrowIfDisposed();
            Check(_content.EnumObjects(0, parentId, 0, out enumerator), "Listing device folders");
        }
        finally
        {
            _lock.Release();
        }

        var ids = new List<string>();
        var batch = new nint[EnumerationBatchSize];
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int hr;
                uint fetched;
                _lock.Wait(cancellationToken);
                try
                {
                    ThrowIfDisposed();
                    fixed (nint* batchPointer = batch)
                    {
                        hr = enumerator.Next(EnumerationBatchSize, batchPointer, out fetched);
                    }
                }
                finally
                {
                    _lock.Release();
                }

                Check(hr, "Listing device folders");
                for (var index = 0; index < fetched; index++)
                {
                    if (ComFactory.TakeString(batch[index]) is { Length: > 0 } id)
                    {
                        ids.Add(id);
                    }
                }

                if (hr == WpdErrors.SFalse || fetched == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            ComFactory.Release(enumerator);
        }

        return ids;
    }

    private bool TryReadObject(string objectId, out ObjectInfo info, out int hr)
    {
        info = default;
        IPortableDeviceValues? values = null;
        _lock.Wait();
        try
        {
            ThrowIfDisposed();
            hr = _properties.GetValues(objectId, _objectKeys, out values);
            if (WpdErrors.Failed(hr))
            {
                ThrowIfDeviceGone(hr);
                return false;
            }

            var name = GetString(values, WpdKeys.ObjectName);
            var originalName = GetString(values, WpdKeys.ObjectOriginalFileName);
            info = new ObjectInfo(
                DisplayName: originalName ?? name ?? objectId,
                FileName: originalName ?? name ?? objectId,
                ContentType: GetGuid(values, WpdKeys.ObjectContentType),
                Size: GetUInt64(values, WpdKeys.ObjectSize) is { } size and <= long.MaxValue ? (long)size : null,
                DateCreated: GetDate(values, WpdKeys.ObjectDateCreated),
                DateModified: GetDate(values, WpdKeys.ObjectDateModified),
                DateAuthored: GetDate(values, WpdKeys.ObjectDateAuthored),
                PersistentId: GetString(values, WpdKeys.ObjectPersistentUniqueId),
                Width: GetUInt32(values, WpdKeys.MediaWidth) is { } width and > 0 and <= int.MaxValue ? (int)width : null,
                Height: GetUInt32(values, WpdKeys.MediaHeight) is { } height and > 0 and <= int.MaxValue ? (int)height : null);
            return true;
        }
        finally
        {
            _lock.Release();
            ComFactory.Release(values);
        }
    }

    private static string? GetString(IPortableDeviceValues values, PropertyKey key) =>
        WpdErrors.Failed(values.GetStringValue(key, out var pointer)) ? null : ComFactory.TakeString(pointer);

    private static Guid GetGuid(IPortableDeviceValues values, PropertyKey key) =>
        WpdErrors.Failed(values.GetGuidValue(key, out var value)) ? Guid.Empty : value;

    private static ulong? GetUInt64(IPortableDeviceValues values, PropertyKey key) =>
        WpdErrors.Failed(values.GetUnsignedLargeIntegerValue(key, out var value)) ? null : value;

    private static uint? GetUInt32(IPortableDeviceValues values, PropertyKey key) =>
        WpdErrors.Failed(values.GetUnsignedIntegerValue(key, out var value)) ? null : value;

    private static DateTimeOffset? GetDate(IPortableDeviceValues values, PropertyKey key)
    {
        if (WpdErrors.Failed(values.GetValue(key, out var variant)))
        {
            return null;
        }

        try
        {
            return variant.VariantType switch
            {
                // VT_DATE carries no time zone; WPD devices report local time.
                PropVariant.VtDate when variant.DoubleValue is > 0 and < 2958466 =>
                    new DateTimeOffset(DateTime.SpecifyKind(DateTime.FromOADate(variant.DoubleValue), DateTimeKind.Local)),
                PropVariant.VtFileTime when variant.Int64Value > 0 =>
                    new DateTimeOffset(DateTime.FromFileTimeUtc(variant.Int64Value)),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            // Out-of-range dates from the device are treated as missing metadata.
            return null;
        }
        finally
        {
            _ = ComFactory.PropVariantClear(ref variant);
        }
    }

    private static void Check(int hr, string operation)
    {
        if (WpdErrors.Failed(hr))
        {
            throw new WpdException(WpdErrors.FromHResult(hr, operation));
        }
    }

    private static void ThrowIfDeviceGone(int hr)
    {
        var error = WpdErrors.FromHResult(hr, "Reading device properties");
        if (error.Kind is Domain.Results.ErrorKind.DeviceDisconnected or Domain.Results.ErrorKind.DeviceLockedOrUntrusted)
        {
            throw new WpdException(error);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private readonly record struct ObjectInfo(
        string DisplayName,
        string FileName,
        Guid ContentType,
        long? Size,
        DateTimeOffset? DateCreated,
        DateTimeOffset? DateModified,
        DateTimeOffset? DateAuthored,
        string? PersistentId,
        int? Width,
        int? Height);
}
