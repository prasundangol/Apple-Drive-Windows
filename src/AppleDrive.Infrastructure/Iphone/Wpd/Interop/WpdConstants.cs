namespace AppleDrive.Infrastructure.Iphone.Wpd.Interop;

/// <summary>Constants from PortableDevice.h / PortableDeviceApi.h.</summary>
internal static class WpdConstants
{
    public const string DeviceObjectId = "DEVICE";

    public const uint StgmRead = 0x00000000;
    public const uint GenericRead = 0x80000000;
    public const uint SecurityImpersonation = 0x00020000;

    public static readonly Guid ClsidPortableDeviceManager = new("0AF10CEC-2ECD-4B92-9581-34F6AE0637F3");

    /// <summary>
    /// PortableDeviceFTM aggregates the free-threaded marshaler, so the device object can be
    /// used from any thread-pool thread.
    /// </summary>
    public static readonly Guid ClsidPortableDeviceFtm = new("F7C0039A-4762-488A-B4B3-760EF9A1BA9B");

    public static readonly Guid ClsidPortableDeviceValues = new("0C15D503-D017-47CE-9016-7B3F978721CC");
    public static readonly Guid ClsidPortableDeviceKeyCollection = new("DE2D022D-2480-43BE-97F0-D1FA2CF98F4F");

    /// <summary>GUID_DEVINTERFACE_WPD.</summary>
    public static readonly Guid WpdDeviceInterfaceClass = new("6AC27878-A6FA-4155-BA85-F98F491D4F33");

    public static readonly Guid ContentTypeFunctionalObject = new("99ED0160-17FF-4C44-9D98-1D7A6F941921");
    public static readonly Guid ContentTypeFolder = new("27E2E392-A111-48E0-AB0C-E17705A05F85");
    public static readonly Guid ContentTypeImage = new("EF2107D5-A52A-4243-A26B-62D4176D7603");
    public static readonly Guid ContentTypeVideo = new("9261B03C-3D78-4519-85E3-02C5E1F50BB9");
}

/// <summary>PROPERTYKEY values from PortableDevice.h.</summary>
internal static class WpdKeys
{
    private static readonly Guid ObjectProperties = new("EF6B490D-5CD8-437A-AFFC-DA8B60EE4A3C");
    private static readonly Guid MediaProperties = new("2ED8BA05-0AD3-42DC-B0D0-BC95AC396AC8");
    private static readonly Guid ClientInfo = new("204D9F0C-2292-4080-9F42-40664E70F859");
    private static readonly Guid ResourceDefault = new("E81E79BE-34F0-41BF-B53F-F1A06AE87842");

    public static readonly PropertyKey ObjectId = new(ObjectProperties, 2);
    public static readonly PropertyKey ObjectParentId = new(ObjectProperties, 3);
    public static readonly PropertyKey ObjectName = new(ObjectProperties, 4);
    public static readonly PropertyKey ObjectPersistentUniqueId = new(ObjectProperties, 5);
    public static readonly PropertyKey ObjectFormat = new(ObjectProperties, 6);
    public static readonly PropertyKey ObjectContentType = new(ObjectProperties, 7);
    public static readonly PropertyKey ObjectSize = new(ObjectProperties, 11);
    public static readonly PropertyKey ObjectOriginalFileName = new(ObjectProperties, 12);
    public static readonly PropertyKey ObjectDateCreated = new(ObjectProperties, 18);
    public static readonly PropertyKey ObjectDateModified = new(ObjectProperties, 19);
    public static readonly PropertyKey ObjectDateAuthored = new(ObjectProperties, 20);

    public static readonly PropertyKey MediaWidth = new(MediaProperties, 22);
    public static readonly PropertyKey MediaHeight = new(MediaProperties, 23);

    public static readonly PropertyKey ClientName = new(ClientInfo, 2);
    public static readonly PropertyKey ClientMajorVersion = new(ClientInfo, 3);
    public static readonly PropertyKey ClientMinorVersion = new(ClientInfo, 4);
    public static readonly PropertyKey ClientRevision = new(ClientInfo, 5);
    public static readonly PropertyKey ClientSecurityQualityOfService = new(ClientInfo, 8);
    public static readonly PropertyKey ClientDesiredAccess = new(ClientInfo, 9);

    public static readonly PropertyKey ResourceDefaultKey = new(ResourceDefault, 0);
}
