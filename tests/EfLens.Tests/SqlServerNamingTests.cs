using EfLens.Providers;

namespace EfLens.Tests;

/// <summary>Pure helpers of the SQL Server provider; no database needed.</summary>
public sealed class SqlServerNamingTests
{
    [Theory]
    [InlineData("Orders", null, "Orders")]
    [InlineData("sales.Orders", "sales", "Orders")]
    [InlineData("[sales].[Orders]", "sales", "Orders")]
    [InlineData(" dbo . Customers ", "dbo", "Customers")]
    public void SplitName_accepts_plain_qualified_and_bracketed_names(string input, string? schema, string name) =>
        Assert.Equal((schema, name), SqlServerSchemaProvider.SplitName(input));

    [Theory]
    [InlineData("nvarchar", 200, 0, 0, "nvarchar(100)")]
    [InlineData("nvarchar", -1, 0, 0, "nvarchar(max)")]
    [InlineData("varchar", 50, 0, 0, "varchar(50)")]
    [InlineData("varbinary", -1, 0, 0, "varbinary(max)")]
    [InlineData("decimal", 9, 18, 4, "decimal(18,4)")]
    [InlineData("datetime2", 8, 27, 7, "datetime2(7)")]
    [InlineData("int", 4, 10, 0, "int")]
    public void FormatType_matches_the_declared_type(string type, short maxLength, byte precision, byte scale, string expected) =>
        Assert.Equal(expected, SqlServerSchemaProvider.FormatType(type, maxLength, precision, scale));
}
