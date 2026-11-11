using EfLens.Providers;
using Microsoft.Data.Sqlite;

namespace EfLens.Tests;

public sealed class SqliteSchemaProviderTests
{
    [Fact]
    public async Task ListTables_returns_tables_and_views_without_system_tables()
    {
        using var db = new TestDatabase();
        var provider = new SqliteSchemaProvider(db.ConnectionString);

        var tables = await provider.ListTablesAsync();

        // AUTOINCREMENT creates sqlite_sequence: it must not leak out.
        Assert.Equal(
            [
                new TableInfo(null, "Customers", "table"),
                new TableInfo(null, "Orders", "table"),
                new TableInfo(null, "CustomerTotals", "view"),
            ],
            tables);
    }

    [Fact]
    public async Task Missing_database_file_fails_instead_of_being_created()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eflens-missing-{Guid.NewGuid():N}.db");
        var provider = new SqliteSchemaProvider($"Data Source={path};Pooling=False");

        await Assert.ThrowsAsync<SqliteException>(() => provider.ListTablesAsync());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Write_mode_requested_by_the_caller_is_overridden_to_read_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eflens-missing-{Guid.NewGuid():N}.db");

        // ReadWriteCreate would create the file; the provider forces ReadOnly, so it must fail.
        var provider = new SqliteSchemaProvider($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");

        await Assert.ThrowsAsync<SqliteException>(() => provider.ListTablesAsync());
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_connection_string_is_rejected(string connectionString) =>
        Assert.Throws<ArgumentException>(() => new SqliteSchemaProvider(connectionString));
}
