using EfLens.Providers;

namespace EfLens.Tests;

public sealed class QueryRowsTests
{
    [Fact]
    public void Binary_values_become_short_hex()
    {
        Assert.Equal("0x0102FF", QueryRows.Normalize(new byte[] { 1, 2, 255 }, 500));
        Assert.Equal($"0x{new string('0', 64)}… (40 bytes)", QueryRows.Normalize(new byte[40], 500));
    }

    [Fact]
    public void Known_scalars_pass_through_and_unknown_types_become_text()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, QueryRows.Normalize(id, 500));
        Assert.Equal(10.5m, QueryRows.Normalize(10.5m, 500));
        Assert.Equal("NaN", QueryRows.Normalize(double.NaN, 500));
        Assert.Equal("http://example.com/", QueryRows.Normalize(new Uri("http://example.com"), 500));
    }
}
