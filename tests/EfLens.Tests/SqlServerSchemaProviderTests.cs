using EfLens.Providers;

namespace EfLens.Tests;

/// <summary>Runs against a real SQL Server in a Testcontainers container.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerSchemaProviderTests(SqlServerFixture fixture)
{
    private SqlServerSchemaProvider Provider
    {
        get
        {
            Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
            return new SqlServerSchemaProvider(fixture.ConnectionString);
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ListTables_includes_the_schema_of_each_object()
    {
        var tables = await Provider.ListTablesAsync(Ct);

        Assert.Equal(
            [
                new TableInfo("dbo", "Audit", "table"),
                new TableInfo("dbo", "Customers", "table"),
                new TableInfo("sales", "Audit", "table"),
                new TableInfo("sales", "OrderLines", "table"),
                new TableInfo("sales", "Orders", "table"),
                new TableInfo("dbo", "CustomerTotals", "view"),
            ],
            tables);
    }

    [Fact]
    public async Task DescribeTable_formats_types_the_way_they_were_declared()
    {
        var customers = await Provider.DescribeTableAsync("Customers", Ct);
        var orders = await Provider.DescribeTableAsync("sales.Orders", Ct);

        Assert.Equal(
            ["int", "nvarchar(100)", "varchar(200)", "nvarchar(max)"],
            customers.Columns.Select(c => c.DataType));
        Assert.Equal(
            ["int", "int", "decimal(10,2)", "datetime2(3)"],
            orders.Columns.Select(c => c.DataType));
    }

    [Fact]
    public async Task DescribeTable_reports_nullability_defaults_and_primary_key()
    {
        var orders = await Provider.DescribeTableAsync("sales.Orders", Ct);

        Assert.Equal("sales", orders.Schema);
        Assert.Equal(new ColumnInfo("Id", "int", false, null, 1), orders.Columns[0]);
        Assert.Equal("((0))", orders.Columns.Single(c => c.Name == "Total").DefaultValue);
        Assert.All(orders.Columns.Skip(1), c => Assert.Null(c.PrimaryKeyOrdinal));
    }

    [Fact]
    public async Task DescribeTable_lists_primary_key_unique_and_regular_indexes()
    {
        var ct = Ct;
        var customers = await Provider.DescribeTableAsync("dbo.Customers", ct);
        var orders = await Provider.DescribeTableAsync("[sales].[Orders]", ct);
        var lines = await Provider.DescribeTableAsync("OrderLines", ct);

        Assert.Equal(
            [("PK_Customers", true, true), ("UQ_Customers_Email", true, false)],
            customers.Indexes.Select(i => (i.Name, i.IsUnique, i.IsPrimaryKey)));
        Assert.Contains(orders.Indexes, i => i is { Name: "IX_Orders_CustomerId", IsUnique: false } && i.Columns.SequenceEqual(["CustomerId"]));
        Assert.Equal(["OrderId", "LineNo"], Assert.Single(lines.Indexes).Columns);
        Assert.Equal([1, 2], lines.Columns.Where(c => c.PrimaryKeyOrdinal is not null).Select(c => c.PrimaryKeyOrdinal!.Value));
    }

    [Fact]
    public async Task DescribeTable_asks_for_the_schema_when_the_name_is_ambiguous()
    {
        var ex = await Assert.ThrowsAsync<SchemaLookupException>(() => Provider.DescribeTableAsync("Audit", Ct));

        Assert.Contains("dbo.Audit", ex.Message);
        Assert.Contains("sales.Audit", ex.Message);
        Assert.Equal("sales", (await Provider.DescribeTableAsync("sales.Audit", Ct)).Schema);
    }

    [Fact]
    public async Task DescribeTable_on_unknown_table_explains_how_to_recover()
    {
        var ex = await Assert.ThrowsAsync<SchemaLookupException>(() => Provider.DescribeTableAsync("Invoices", Ct));

        Assert.Contains("list_tables", ex.Message);
    }

    [Fact]
    public async Task GetRelations_returns_named_foreign_keys_across_schemas()
    {
        var relations = await Provider.GetRelationsAsync(table: null, Ct);

        Assert.Equal(
            [
                new ForeignKeyInfo("FK_OrderLines_Orders", "sales", "OrderLines", ["OrderId"], "sales", "Orders", ["Id"], "NO ACTION"),
                new ForeignKeyInfo("FK_Orders_Customers", "sales", "Orders", ["CustomerId"], "dbo", "Customers", ["Id"], "CASCADE"),
            ],
            relations,
            ForeignKeyComparer.Instance);
    }

    [Fact]
    public async Task GetRelations_for_a_table_includes_foreign_keys_that_reference_it()
    {
        var relations = await Provider.GetRelationsAsync("Customers", Ct);

        Assert.Equal("FK_Orders_Customers", Assert.Single(relations).Name);
    }

    /// <summary>Records compare lists by reference; this compares them by content.</summary>
    private sealed class ForeignKeyComparer : IEqualityComparer<ForeignKeyInfo>
    {
        public static readonly ForeignKeyComparer Instance = new();

        public bool Equals(ForeignKeyInfo? x, ForeignKeyInfo? y) =>
            x is not null && y is not null
            && x with { FromColumns = [], ToColumns = [] } == y with { FromColumns = [], ToColumns = [] }
            && x.FromColumns.SequenceEqual(y.FromColumns)
            && x.ToColumns.SequenceEqual(y.ToColumns);

        public int GetHashCode(ForeignKeyInfo obj) => HashCode.Combine(obj.Name, obj.FromTable, obj.ToTable);
    }
}
