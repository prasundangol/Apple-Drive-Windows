using System.Security.Cryptography;
using AppleDrive.Infrastructure.Hashing;
using AppleDrive.Testing;

namespace AppleDrive.UnitTests.Infrastructure;

public sealed class Sha256HashServiceTests : IDisposable
{
    private readonly TemporaryDirectory _directory = new();
    private readonly Sha256HashService _service = new();

    [Fact]
    public async Task Same_content_gives_same_hash()
    {
        var content = RandomNumberGenerator.GetBytes(3 * 1024 * 1024 + 17);
        var first = Write("a.bin", content);
        var second = Write("b.bin", content);

        var a = await _service.ComputeFileAsync(first, Ct);
        var b = await _service.ComputeFileAsync(second, Ct);

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(content.Length, a.Length);
        Assert.Equal(SHA256.HashData(content), a.Sha256);
    }

    [Fact]
    public async Task One_changed_byte_gives_a_different_hash()
    {
        var content = RandomNumberGenerator.GetBytes(10_000);
        var changed = (byte[])content.Clone();
        changed[9_999] ^= 1;

        var a = await _service.ComputeFileAsync(Write("a.bin", content), Ct);
        var b = await _service.ComputeFileAsync(Write("b.bin", changed), Ct);

        Assert.NotEqual(a.Sha256, b.Sha256);
        Assert.False(a.Matches(b.Sha256));
    }

    [Fact]
    public async Task Empty_stream_has_the_standard_empty_digest()
    {
        var result = await _service.ComputeAsync(new MemoryStream(), null, Ct);

        Assert.Equal(0, result.Length);
        Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", Convert.ToHexString(result.Sha256));
    }

    [Fact]
    public async Task Reports_bytes_read()
    {
        var reports = new List<long>();
        var progress = new SynchronousProgress<long>(reports.Add);

        await _service.ComputeAsync(new MemoryStream(new byte[2_500_000]), progress, Ct);

        Assert.Equal(2_500_000, reports[^1]);
    }

    private string Write(string name, byte[] content)
    {
        var path = _directory.Combine(name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _directory.Dispose();
}
