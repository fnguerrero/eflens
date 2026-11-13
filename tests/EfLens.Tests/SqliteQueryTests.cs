using EfLens.Providers;

namespace EfLens.Tests;

public sealed class SqliteQueryTests : IDisposable
{
    private static readonly QueryLimits Limits = new(MaxRows: 50, Timeout: TimeSpan.FromSeconds(5));

    private readonly TestDatabase _db = new();
    private readonly SqliteSchemaProvider _provider;

    public SqliteQueryTests() => _provider = new SqliteSchemaProvider(_db.ConnectionString);

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_column_names_and_rows_as_arrays()
    {
        var result = await _provider.QueryAsync("SELECT Id, Name, Email FROM Customers ORDER BY Id", Limits, Ct);

        Assert.Equal(["Id", "Name", "Email"], result.Columns);
        Assert.Equal([1L, "Ada", null], Assert.Single(result.Rows));
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Supports_ctes_joins_and_aggregates()
    {
        var result = await _provider.QueryAsync("""
            WITH totals AS (SELECT CustomerId, COUNT(*) AS n FROM Orders GROUP BY CustomerId)
            SELECT c.Name, COALESCE(t.n, 0) AS Orders
            FROM Customers c LEFT JOIN totals t ON t.CustomerId = c.Id
            """, Limits, Ct);

        Assert.Equal(["Name", "Orders"], result.Columns);
        Assert.Equal(["Ada", 0L], Assert.Single(result.Rows));
    }

    [Fact]
    public async Task Stops_at_max_rows_and_says_it_truncated()
    {
        var result = await _provider.QueryAsync(
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 10) SELECT x FROM n",
            Limits with { MaxRows = 3 }, Ct);

        Assert.Equal([1L, 2L, 3L], result.Rows.Select(r => r[0]));
        Assert.True(result.Truncated);
    }

    [Theory]
    [InlineData("INSERT INTO Customers (Name) VALUES ('Mallory')", "INSERT")]
    [InlineData("UPDATE Customers SET Name = 'Mallory'", "UPDATE")]
    [InlineData("DELETE FROM Orders", "DELETE")]
    [InlineData("DROP TABLE Orders", "Changing the schema")]
    [InlineData("CREATE TABLE Evil (x)", "Changing the schema")]
    [InlineData("PRAGMA writable_schema = 1", "PRAGMA")]
    [InlineData("ATTACH DATABASE 'other.db' AS other", "ATTACH")]
    [InlineData("BEGIN IMMEDIATE", "Transaction control")]
    [InlineData("SELECT 1; DELETE FROM Orders", "more than one statement")]
    [InlineData("SELECT 1; SELECT 2", "more than one statement")]
    [InlineData("   ", "empty")]
    [InlineData("-- only a comment", "no statement")]
    public async Task The_engine_rejects_anything_but_one_read(string sql, string reason)
    {
        var ex = await Assert.ThrowsAsync<QueryRejectedException>(() => _provider.QueryAsync(sql, Limits, Ct));

        Assert.Contains(reason, ex.Message);
        var customers = await _provider.QueryAsync("SELECT Name FROM Customers", Limits, Ct);
        Assert.Equal(["Ada"], customers.Rows.Select(r => r[0]));
    }

    [Fact]
    public async Task Trailing_semicolons_and_comments_are_fine()
    {
        var result = await _provider.QueryAsync("SELECT 42; -- the answer", Limits, Ct);

        Assert.Equal(42L, Assert.Single(result.Rows)[0]);
    }

    [Fact]
    public async Task A_runaway_query_is_interrupted_inside_the_engine()
    {
        var limits = Limits with { Timeout = TimeSpan.FromMilliseconds(300) };

        var ex = await Assert.ThrowsAsync<QueryTimeoutException>(() => _provider.QueryAsync(
            "WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n) SELECT COUNT(*) FROM n", limits, Ct));

        Assert.Contains("0.3 s", ex.Message);
    }

    [Fact]
    public async Task Engine_errors_reach_the_agent_so_it_can_fix_the_query()
    {
        var ex = await Assert.ThrowsAsync<QueryFailedException>(
            () => _provider.QueryAsync("SELECT Nickname FROM Customers", Limits, Ct));

        Assert.Contains("no such column", ex.Message);
    }

    [Fact]
    public async Task Long_text_and_non_finite_numbers_are_made_safe()
    {
        var result = await _provider.QueryAsync("SELECT printf('%.*c', 1000, 'x'), 1e999", Limits with { MaxCellLength = 10 }, Ct);

        var row = Assert.Single(result.Rows);
        Assert.Equal("xxxxxxxxxx… (1000 chars)", row[0]);
        Assert.Equal("Infinity", row[1]);
    }

    [Fact]
    public async Task Schema_tools_keep_working_after_a_query()
    {
        await _provider.QueryAsync("SELECT 1", Limits, Ct);

        // The authorizer lives on the query connection only; pragma-based schema calls must not be affected.
        Assert.Equal(3, (await _provider.DescribeTableAsync("Orders", Ct)).Columns.Count);
    }
}
