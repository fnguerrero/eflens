using EfLens.Providers;

namespace EfLens.Tests;

/// <summary>Schema tools, query, explain and migration history against a real PostgreSQL in a container.</summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresTests(PostgresFixture fixture)
{
    private static readonly QueryLimits Limits = new(MaxRows: 50, Timeout: TimeSpan.FromSeconds(10));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private PostgresSchemaProvider Provider
    {
        get
        {
            Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
            return new PostgresSchemaProvider(fixture.ConnectionString);
        }
    }

    // ---- schema --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ListTables_includes_the_schema_and_puts_views_last()
    {
        var tables = await Provider.ListTablesAsync(Ct);

        Assert.Equal(
            [
                ("ef", "__EFMigrationsHistory", "table"),
                ("public", "Audit", "table"), ("public", "Customers", "table"),
                ("sales", "Audit", "table"), ("sales", "OrderLines", "table"), ("sales", "Orders", "table"),
                ("public", "CustomerTotals", "view"),
            ],
            tables.Select(t => (t.Schema!, t.Name, t.Kind)));
    }

    [Fact]
    public async Task DescribeTable_reports_types_defaults_keys_and_indexes()
    {
        var orders = await Provider.DescribeTableAsync("sales.Orders", Ct);

        Assert.Equal(
            [
                ("Id", "integer", false, (int?)1),
                ("CustomerId", "integer", false, null),
                ("Total", "numeric(10,2)", false, null),
                ("PlacedAt", "timestamp(3) without time zone", false, null),
            ],
            orders.Columns.Select(c => (c.Name, c.DataType, c.IsNullable, c.PrimaryKeyOrdinal)));
        Assert.Equal("0", orders.Columns.Single(c => c.Name == "Total").DefaultValue);
        Assert.Equal(
            [("IX_Orders_CustomerId", "CustomerId", false, false), ("PK_Orders", "Id", true, true)],
            orders.Indexes.Select(i => (i.Name, string.Join(",", i.Columns), i.IsUnique, i.IsPrimaryKey)));

        var lines = await Provider.DescribeTableAsync("""
            "sales"."OrderLines"
            """, Ct);
        Assert.Equal(["OrderId", "LineNumber"], lines.Indexes.Single(i => i.IsPrimaryKey).Columns);
    }

    [Fact]
    public async Task Names_resolve_without_quotes_and_ambiguity_is_explained()
    {
        Assert.Equal(("sales", "Orders"), await Provider.DescribeTableAsync("orders", Ct) is var t ? (t.Schema, t.Name) : default);

        var ambiguous = await Assert.ThrowsAsync<SchemaLookupException>(() => Provider.DescribeTableAsync("Audit", Ct));
        Assert.Contains("public.Audit, sales.Audit", ambiguous.Message);

        var missing = await Assert.ThrowsAsync<SchemaLookupException>(() => Provider.DescribeTableAsync("Invoices", Ct));
        Assert.Contains("list_tables", missing.Message);
    }

    [Fact]
    public async Task Relations_carry_columns_on_delete_and_schemas()
    {
        var all = await Provider.GetRelationsAsync(null, Ct);
        Assert.Equal(["FK_OrderLines_Orders", "FK_Orders_Customers"], all.Select(r => r.Name!));

        var fk = Assert.Single(await Provider.GetRelationsAsync("Customers", Ct));
        Assert.Equal(("sales", "Orders", "CustomerId", "public", "Customers", "Id", "CASCADE"),
            (fk.FromSchema, fk.FromTable, fk.FromColumns.Single(), fk.ToSchema, fk.ToTable, fk.ToColumns.Single(), fk.OnDelete));
    }

    [Fact]
    public async Task Migration_history_is_read_from_the_schema_ef_core_uses()
    {
        Assert.Null(await Provider.GetAppliedMigrationsAsync(null, "__EFMigrationsHistory", Ct));
        Assert.Equal(["001_InitialCreate", "002_AddOrderStatus"],
            await Provider.GetAppliedMigrationsAsync("ef", "__EFMigrationsHistory", Ct));
    }

    // ---- query ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Returns_typed_values_across_schemas()
    {
        var result = await Provider.QueryAsync("""
            SELECT c."Name", o."Total", o."PlacedAt"
            FROM sales."Orders" AS o JOIN "Customers" AS c ON c."Id" = o."CustomerId"
            ORDER BY o."Id";
            """, Limits, Ct);

        Assert.Equal(["Name", "Total", "PlacedAt"], result.Columns);
        Assert.Equal(["Ada", 10.50m, new DateTime(2026, 1, 1, 10, 0, 0)], result.Rows[0]);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Rows_stop_at_the_cap_without_computing_the_rest()
    {
        // Ten billion rows, produced one at a time by a nested loop: only the cursor's FETCH stops it from running for hours.
        var result = await Provider.QueryAsync(
            "SELECT a.x FROM generate_series(1, 100000) AS a(x) CROSS JOIN generate_series(1, 100000) AS b(y)",
            Limits with { MaxRows = 3 }, Ct);

        Assert.Equal(3, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.True(result.ElapsedMs < 5000, $"took {result.ElapsedMs} ms");
    }

    [Fact]
    public async Task A_runaway_query_is_cancelled_by_the_server()
    {
        await Assert.ThrowsAsync<QueryTimeoutException>(() =>
            Provider.QueryAsync("SELECT pg_sleep(30)", Limits with { Timeout = TimeSpan.FromSeconds(1) }, Ct));
    }

    [Theory]
    [InlineData("""DELETE FROM sales."Orders" """, "DELETE is not allowed")]
    [InlineData("""UPDATE "Customers" SET "Name" = 'x'""", "UPDATE is not allowed")]
    [InlineData("""INSERT INTO public."Audit" VALUES (1)""", "INSERT is not allowed")]
    [InlineData("""WITH gone AS (DELETE FROM sales."Orders" RETURNING 1) SELECT count(*) FROM gone""", "DELETE is not allowed")]
    [InlineData("""SELECT 1; DELETE FROM sales."Orders" """, "more than one statement")]
    [InlineData("""SELECT 1; COMMIT; DELETE FROM sales."Orders" """, "more than one statement")]
    [InlineData("""SELECT * FROM "Customers" FOR UPDATE""", "row locks")]
    [InlineData("""SELECT nextval('"Customers_Id_seq"')""", "refused to write")]
    [InlineData("""CREATE TABLE stolen AS SELECT * FROM "Customers" """, "syntax error")]
    [InlineData("""DO $$ BEGIN DELETE FROM sales."Orders"; END $$""", "syntax error")]
    [InlineData("COMMIT", "syntax error")]
    [InlineData("SET ROLE postgres", "syntax error")]
    [InlineData("""COPY "Customers" TO STDOUT""", "syntax error")]
    public async Task Anything_but_one_read_only_select_is_rejected(string sql, string reason)
    {
        var ex = await Assert.ThrowsAsync<QueryRejectedException>(() => Provider.QueryAsync(sql, Limits, Ct));
        Assert.Contains(reason, ex.Message);

        var count = await Provider.QueryAsync("""SELECT count(*) FROM sales."Orders" """, Limits, Ct);
        Assert.Equal(2L, count.Rows[0][0]);
    }

    [Fact]
    public async Task Engine_errors_reach_the_agent_so_it_can_fix_the_query()
    {
        var ex = await Assert.ThrowsAsync<QueryFailedException>(() =>
            Provider.QueryAsync("""SELECT "Nope" FROM "Customers" """, Limits, Ct));
        Assert.Contains("\"Nope\" does not exist", ex.Message);
    }

    // ---- explain -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Explain_returns_the_plan_tree_without_running_the_query()
    {
        var plan = await Provider.ExplainAsync("""
            SELECT c."Name", o."Total" FROM sales."Orders" AS o JOIN "Customers" AS c ON c."Id" = o."CustomerId"
            WHERE o."Total" > 1
            """, Limits, Ct);

        Assert.True(plan.EstimatedCost > 0);
        var nodes = Flatten(plan.Plan).ToList();
        Assert.Contains(nodes, n => n.Object?.StartsWith("public.Customers", StringComparison.Ordinal) == true);
        Assert.Contains(nodes, n => n.Object?.StartsWith("sales.Orders", StringComparison.Ordinal) == true);
        Assert.Contains(plan.Notes, n => n.StartsWith("Seq Scan on sales.Orders filtered by", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Explain_follows_the_same_rules_as_query()
    {
        var ex = await Assert.ThrowsAsync<QueryRejectedException>(() => Provider.ExplainAsync("""DELETE FROM sales."Orders" """, Limits, Ct));
        Assert.Contains("DELETE is not allowed", ex.Message);
    }

    private static IEnumerable<PlanNode> Flatten(IEnumerable<PlanNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
