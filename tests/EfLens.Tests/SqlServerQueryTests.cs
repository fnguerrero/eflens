using EfLens.Providers;

namespace EfLens.Tests;

/// <summary>The query tool against a real SQL Server in a Testcontainers container.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerQueryTests(SqlServerFixture fixture)
{
    private static readonly QueryLimits Limits = new(MaxRows: 50, Timeout: TimeSpan.FromSeconds(10));

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
    public async Task Returns_typed_values_across_schemas()
    {
        var result = await Provider.QueryAsync("""
            SELECT c.Name, o.Total, o.PlacedAt
            FROM sales.Orders AS o JOIN dbo.Customers AS c ON c.Id = o.CustomerId
            ORDER BY o.Id
            """, Limits, Ct);

        Assert.Equal(["Name", "Total", "PlacedAt"], result.Columns);
        Assert.Equal(["Ada", 10.50m, new DateTime(2026, 1, 1, 10, 0, 0)], result.Rows[0]);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public async Task Runs_inside_a_transaction_that_is_rolled_back()
    {
        var result = await Provider.QueryAsync("SELECT @@TRANCOUNT AS OpenTransactions", Limits, Ct);

        Assert.Equal(1, Assert.Single(result.Rows)[0]);
    }

    [Fact]
    public async Task Stops_reading_at_max_rows_without_draining_the_rest()
    {
        var result = await Provider.QueryAsync(
            "SELECT a.object_id FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b",
            Limits with { MaxRows = 5 }, Ct);

        Assert.Equal(5, result.Rows.Count);
        Assert.True(result.Truncated);
        // Millions of rows exist; draining them would take far longer than this.
        Assert.True(result.ElapsedMs < 5_000, $"took {result.ElapsedMs} ms");
    }

    [Fact]
    public async Task A_runaway_query_is_cancelled_on_the_server()
    {
        var ex = await Assert.ThrowsAsync<QueryTimeoutException>(() => Provider.QueryAsync(
            "SELECT COUNT_BIG(*) FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b CROSS JOIN sys.all_objects AS c",
            Limits with { Timeout = TimeSpan.FromSeconds(1) }, Ct));

        Assert.Contains("1 s", ex.Message);
    }

    [Fact]
    public async Task Writes_are_rejected_before_reaching_the_server()
    {
        await Assert.ThrowsAsync<QueryRejectedException>(
            () => Provider.QueryAsync("DELETE FROM sales.OrderLines; SELECT 1", Limits, Ct));

        var count = await Provider.QueryAsync("SELECT COUNT(*) FROM dbo.Customers", Limits, Ct);
        Assert.Equal(2, Assert.Single(count.Rows)[0]);
    }

    [Fact]
    public async Task Engine_errors_reach_the_agent_so_it_can_fix_the_query()
    {
        var ex = await Assert.ThrowsAsync<QueryFailedException>(
            () => Provider.QueryAsync("SELECT Nickname FROM dbo.Customers", Limits, Ct));

        Assert.Contains("Invalid column name 'Nickname'", ex.Message);
    }
}
