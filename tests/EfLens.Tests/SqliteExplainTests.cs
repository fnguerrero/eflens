using EfLens.Providers;

namespace EfLens.Tests;

public sealed class SqliteExplainTests : IDisposable
{
    private static readonly QueryLimits Limits = new(MaxRows: 1, Timeout: TimeSpan.FromSeconds(5));

    private readonly TestDatabase _db = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _db.Dispose();

    private SqliteSchemaProvider Provider => new(_db.ConnectionString);

    [Fact]
    public async Task Shows_when_an_index_is_used_and_flags_full_scans()
    {
        var seek = await Provider.ExplainAsync("SELECT Total FROM Orders WHERE CustomerId = 1", Limits, Ct);
        Assert.Contains("USING INDEX IX_Orders_CustomerId", Assert.Single(seek.Plan).Operation);
        Assert.Empty(seek.Notes);

        var scan = await Provider.ExplainAsync("SELECT Id FROM Customers WHERE Name = 'Ada'", Limits, Ct);
        Assert.StartsWith("SCAN Customers", Assert.Single(scan.Plan).Operation);
        Assert.Contains("full scan", Assert.Single(scan.Notes));
    }

    [Fact]
    public async Task Writes_are_rejected_by_the_same_authorizer_as_query()
    {
        var ex = await Assert.ThrowsAsync<QueryRejectedException>(() => Provider.ExplainAsync("DELETE FROM Orders", Limits, Ct));
        Assert.Contains("DELETE is not allowed", ex.Message);
    }
}
