using EfLens.Providers;
using Microsoft.Data.Sqlite;

namespace EfLens.Tests;

public sealed class SqliteSchemaProviderTests : IDisposable
{
    private readonly TestDatabase _db = new();
    private readonly SqliteSchemaProvider _provider;

    public SqliteSchemaProviderTests() => _provider = new SqliteSchemaProvider(_db.ConnectionString);

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ListTables_returns_tables_and_views_without_system_tables()
    {
        var tables = await _provider.ListTablesAsync(TestContext.Current.CancellationToken);

        // AUTOINCREMENT creates sqlite_sequence: it must not leak out.
        Assert.Equal(
            [
                new TableInfo(null, "Customers", "table"),
                new TableInfo(null, "OrderLines", "table"),
                new TableInfo(null, "Orders", "table"),
                new TableInfo(null, "CustomerTotals", "view"),
            ],
            tables);
    }

    [Fact]
    public async Task DescribeTable_returns_columns_with_types_nullability_defaults_and_primary_key()
    {
        var orders = await _provider.DescribeTableAsync("Orders", TestContext.Current.CancellationToken);

        Assert.Equal("table", orders.Kind);
        Assert.Equal(
            [
                new ColumnInfo("Id", "INTEGER", IsNullable: false, DefaultValue: null, PrimaryKeyOrdinal: 1),
                new ColumnInfo("CustomerId", "INTEGER", IsNullable: false, DefaultValue: null, PrimaryKeyOrdinal: null),
                new ColumnInfo("Total", "REAL", IsNullable: true, DefaultValue: "0", PrimaryKeyOrdinal: null),
            ],
            orders.Columns);
        var index = Assert.Single(orders.Indexes);
        Assert.Equal("IX_Orders_CustomerId", index.Name);
        Assert.Equal(["CustomerId"], index.Columns);
        Assert.False(index.IsUnique);
    }

    [Fact]
    public async Task DescribeTable_reports_composite_primary_keys_and_unique_constraints()
    {
        var ct = TestContext.Current.CancellationToken;
        var lines = await _provider.DescribeTableAsync("OrderLines", ct);
        var customers = await _provider.DescribeTableAsync("Customers", ct);

        Assert.Equal([1, 2], lines.Columns.Where(c => c.PrimaryKeyOrdinal is not null).Select(c => c.PrimaryKeyOrdinal!.Value));
        var pk = Assert.Single(lines.Indexes);
        Assert.True(pk.IsPrimaryKey);
        Assert.Equal(["OrderId", "LineNo"], pk.Columns);

        var unique = Assert.Single(customers.Indexes);
        Assert.True(unique.IsUnique);
        Assert.False(unique.IsPrimaryKey);
        Assert.Equal(["Email"], unique.Columns);
    }

    [Fact]
    public async Task DescribeTable_is_case_insensitive_and_works_on_views()
    {
        var view = await _provider.DescribeTableAsync("customertotals", TestContext.Current.CancellationToken);

        Assert.Equal("CustomerTotals", view.Name);
        Assert.Equal("view", view.Kind);
        Assert.Equal(["CustomerId", "Total"], view.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task DescribeTable_on_unknown_table_explains_how_to_recover()
    {
        var ex = await Assert.ThrowsAsync<SchemaLookupException>(
            () => _provider.DescribeTableAsync("Invoices", TestContext.Current.CancellationToken));

        Assert.Contains("Invoices", ex.Message);
        Assert.Contains("list_tables", ex.Message);
    }

    [Fact]
    public async Task Table_names_are_bound_as_parameters_not_concatenated()
    {
        var ex = await Assert.ThrowsAsync<SchemaLookupException>(
            () => _provider.DescribeTableAsync("Orders'); DROP TABLE Orders; --", TestContext.Current.CancellationToken));

        Assert.Contains("not found", ex.Message);
        Assert.Contains(await _provider.ListTablesAsync(TestContext.Current.CancellationToken), t => t.Name == "Orders");
    }

    [Fact]
    public async Task GetRelations_returns_every_foreign_key_with_its_delete_action()
    {
        var relations = await _provider.GetRelationsAsync(table: null, TestContext.Current.CancellationToken);

        Assert.Collection(relations,
            lines =>
            {
                Assert.Equal(("OrderLines", "Orders"), (lines.FromTable, lines.ToTable));
                Assert.Equal(["OrderId"], lines.FromColumns);
                // "REFERENCES Orders" has no column list: it resolves to the target's primary key.
                Assert.Equal(["Id"], lines.ToColumns);
                Assert.Equal("NO ACTION", lines.OnDelete);
            },
            orders =>
            {
                Assert.Equal(("Orders", "Customers"), (orders.FromTable, orders.ToTable));
                Assert.Equal(["CustomerId"], orders.FromColumns);
                Assert.Equal(["Id"], orders.ToColumns);
                Assert.Equal("CASCADE", orders.OnDelete);
            });
    }

    [Fact]
    public async Task GetRelations_for_a_table_includes_foreign_keys_in_both_directions()
    {
        var relations = await _provider.GetRelationsAsync("Orders", TestContext.Current.CancellationToken);

        Assert.Equal(
            [("OrderLines", "Orders"), ("Orders", "Customers")],
            relations.Select(r => (r.FromTable, r.ToTable)));
    }

    [Fact]
    public async Task Missing_database_file_fails_instead_of_being_created()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eflens-missing-{Guid.NewGuid():N}.db");
        var provider = new SqliteSchemaProvider($"Data Source={path};Pooling=False");

        await Assert.ThrowsAsync<SqliteException>(() => provider.ListTablesAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Write_mode_requested_by_the_caller_is_overridden_to_read_only()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eflens-missing-{Guid.NewGuid():N}.db");

        // ReadWriteCreate would create the file; the provider forces ReadOnly, so it must fail.
        var provider = new SqliteSchemaProvider($"Data Source={path};Mode=ReadWriteCreate;Pooling=False");

        await Assert.ThrowsAsync<SqliteException>(() => provider.ListTablesAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_connection_string_is_rejected(string connectionString) =>
        Assert.Throws<ArgumentException>(() => new SqliteSchemaProvider(connectionString));
}
