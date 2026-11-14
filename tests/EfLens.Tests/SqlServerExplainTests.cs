using EfLens.Providers;
using Microsoft.Data.SqlClient;

namespace EfLens.Tests;

[Collection(SqlServerCollection.Name)]
public sealed class SqlServerExplainTests(SqlServerFixture fixture)
{
    private static readonly QueryLimits Limits = new(MaxRows: 1, Timeout: TimeSpan.FromSeconds(10));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SqlServerSchemaProvider Provider(string? database = null)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        return new SqlServerSchemaProvider(database is null
            ? fixture.ConnectionString
            : new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = database }.ConnectionString);
    }

    [Fact]
    public async Task Returns_the_estimated_plan_with_objects_rows_and_cost()
    {
        var plan = await Provider().ExplainAsync("""
            SELECT c.Name, o.Total FROM sales.Orders AS o JOIN dbo.Customers AS c ON c.Id = o.CustomerId
            """, Limits, Ct);

        Assert.True(plan.EstimatedCost > 0);
        var nodes = Flatten(plan.Plan).ToList();
        Assert.Contains(nodes, n => n.Object?.StartsWith("sales.Orders", StringComparison.Ordinal) == true);
        Assert.Contains(nodes, n => n.Object?.StartsWith("dbo.Customers", StringComparison.Ordinal) == true);
        Assert.All(nodes, n => Assert.NotNull(n.EstimatedRows));
    }

    [Fact]
    public async Task Missing_indexes_suggested_by_the_optimizer_become_notes()
    {
        var master = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync(Ct);
            foreach (var sql in new[]
            {
                "DROP DATABASE IF EXISTS ExplainLab",
                "CREATE DATABASE ExplainLab",
                "CREATE TABLE ExplainLab.dbo.Events (Id int IDENTITY PRIMARY KEY, Kind int NOT NULL, Source int NOT NULL, Payload nvarchar(100) NOT NULL)",
                """
                INSERT INTO ExplainLab.dbo.Events (Kind, Source, Payload)
                SELECT TOP (50000) ABS(CHECKSUM(NEWID())) % 100, ABS(CHECKSUM(NEWID())) % 10, N'event'
                FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b
                """,
            })
                await new SqlCommand(sql, connection).ExecuteNonQueryAsync(Ct);
        }

        var plan = await Provider("ExplainLab").ExplainAsync(
            "SELECT Source, COUNT(*) FROM dbo.Events WHERE Kind = 7 GROUP BY Source", Limits, Ct);

        Assert.Contains(plan.Notes, n => n.StartsWith("Missing index", StringComparison.Ordinal) && n.Contains("dbo.Events (Kind)"));
    }

    [Fact]
    public async Task Explain_follows_the_same_rules_as_query()
    {
        var ex = await Assert.ThrowsAsync<QueryRejectedException>(() => Provider().ExplainAsync("DELETE FROM sales.Orders", Limits, Ct));
        Assert.Contains("DELETE", ex.Message);
    }

    private static IEnumerable<PlanNode> Flatten(IEnumerable<PlanNode> nodes) =>
        nodes.SelectMany(n => Flatten(n.Children).Prepend(n));
}
