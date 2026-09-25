using AppleDrive.Domain.Results;
using AppleDrive.Infrastructure.Iphone.Wpd;

namespace AppleDrive.UnitTests.Infrastructure;

public sealed class WpdErrorsTests
{
    [Theory]
    [InlineData(0x80070005u, ErrorKind.DeviceLockedOrUntrusted)]
    [InlineData(0x8007048Fu, ErrorKind.DeviceDisconnected)]
    [InlineData(0x800701B1u, ErrorKind.DeviceDisconnected)]
    [InlineData(0x800700AAu, ErrorKind.DeviceBusy)]
    [InlineData(0x80070079u, ErrorKind.DeviceBusy)]
    [InlineData(0x80070490u, ErrorKind.SourceUnavailable)]
    [InlineData(0x800704C7u, ErrorKind.Cancelled)]
    [InlineData(0x8007001Fu, ErrorKind.DeviceIo)]
    [InlineData(0x80042007u, ErrorKind.DeviceIo)]
    public void Maps_hresults_to_error_kinds(uint hr, ErrorKind expected)
    {
        var error = WpdErrors.FromHResult(unchecked((int)hr), "Test");

        Assert.Equal(expected, error.Kind);
        Assert.Equal(unchecked((int)hr), error.HResult);
    }
}
