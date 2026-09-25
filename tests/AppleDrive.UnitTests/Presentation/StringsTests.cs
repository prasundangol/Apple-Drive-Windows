using System.Globalization;
using System.Reflection;
using AppleDrive.Presentation.Formatting;
using AppleDrive.Presentation.Resources;

namespace AppleDrive.UnitTests.Presentation;

public sealed class StringsTests
{
    public static TheoryData<string> StringProperties()
    {
        var data = new TheoryData<string>();
        foreach (var property in typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            data.Add(property.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(StringProperties))]
    public void Every_string_has_a_resource_value(string name)
    {
        var resources = new System.Resources.ResourceManager("AppleDrive.Presentation.Resources.Strings", typeof(Strings).Assembly);

        var value = resources.GetString(name, CultureInfo.InvariantCulture);

        Assert.False(string.IsNullOrWhiteSpace(value), $"Strings.resx has no value for '{name}'.");
    }

    [Fact]
    public void No_user_facing_string_mentions_internal_terms()
    {
        foreach (var property in typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static))
        {
            var value = (string)property.GetValue(null)!;
            Assert.DoesNotContain("HRESULT", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WPD", value, StringComparison.Ordinal);
            Assert.DoesNotContain("COM ", value, StringComparison.Ordinal);
        }
    }
}

public sealed class ByteSizeTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(999L, "999 B")]
    [InlineData(1_000L, "1 KB")]
    [InlineData(4_821_342L, "4.8 MB")]
    [InlineData(4_308_188_358L, "4.3 GB")]
    [InlineData(38_400_000_000L, "38.4 GB")]
    public void Formats_decimal_units(long bytes, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, ByteSize.Format(bytes));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
