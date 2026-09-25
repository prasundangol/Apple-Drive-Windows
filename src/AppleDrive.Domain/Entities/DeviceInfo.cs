namespace AppleDrive.Domain.Entities;

/// <summary>A phone attached to the computer.</summary>
/// <param name="Id">Platform device identifier (PnP device path). Stable while attached.</param>
/// <param name="FriendlyName">Name shown to the user, for example the phone's own name.</param>
/// <param name="Manufacturer">Manufacturer reported by the driver.</param>
public sealed record DeviceInfo(string Id, string FriendlyName, string Manufacturer);
