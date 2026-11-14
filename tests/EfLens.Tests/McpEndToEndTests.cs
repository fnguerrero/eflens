using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace EfLens.Tests;

/// <summary>Talks to the real server over the MCP protocol, with in-memory pipes instead of stdio.</summary>
public sealed class McpEndToEndTests : IAsyncLifetime
{
    private readonly TestDatabase _db = new();
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private IHost _host = null!;
    private McpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddEfLens(new EfLensOptions
        {
            Provider = "sqlite",
            ConnectionString = _db.ConnectionString,
            EfAssembly = ShopSample.AssemblyPath,
        });
        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream())
            .WithEfLensTools();

        _host = builder.Build();
        await _host.StartAsync();

        _client = await McpClient.CreateAsync(
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream(), loggerFactory: null));
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Server_advertises_every_tool_as_read_only()
    {
        var tools = await _client.ListToolsAsync(cancellationToken: Ct);

        Assert.Equal(["describe_table", "ef_drift", "ef_model", "list_relations", "list_tables", "query"], tools.Select(t => t.Name).Order());
        Assert.All(tools, tool =>
        {
            Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
            Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);
        });
    }

    [Fact]
    public async Task Calling_list_tables_returns_the_schema_as_json()
    {
        var names = (await CallAsync("list_tables")).EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(["Customers", "OrderLines", "Orders", "CustomerTotals"], names);
    }

    [Fact]
    public async Task Calling_describe_table_returns_columns_and_indexes()
    {
        var orders = await CallAsync("describe_table", new() { ["table"] = "Orders" });

        Assert.Equal(["Id", "CustomerId", "Total"],
            orders.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));
        Assert.Equal("IX_Orders_CustomerId",
            Assert.Single(orders.GetProperty("indexes").EnumerateArray()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task Calling_list_relations_with_a_table_filters_the_foreign_keys()
    {
        var relations = await CallAsync("list_relations", new() { ["table"] = "Customers" });

        var fk = Assert.Single(relations.EnumerateArray());
        Assert.Equal("Orders", fk.GetProperty("fromTable").GetString());
        Assert.Equal("CASCADE", fk.GetProperty("onDelete").GetString());
    }

    [Fact]
    public async Task Unknown_table_comes_back_as_a_tool_error_the_agent_can_act_on()
    {
        var result = await _client.CallToolAsync("describe_table",
            new Dictionary<string, object?> { ["table"] = "Invoices" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("Invoices", message);
        Assert.Contains("list_tables", message);
    }

    [Fact]
    public async Task Calling_query_returns_columns_rows_and_the_truncation_flag()
    {
        var result = await CallAsync("query", new()
        {
            ["sql"] = "SELECT name FROM sqlite_schema WHERE type = 'table' ORDER BY name",
            ["maxRows"] = 2,
        });

        Assert.Equal(["name"], result.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!));
        Assert.Equal(2, result.GetProperty("rows").GetArrayLength());
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task A_write_through_query_comes_back_as_a_tool_error_with_the_reason()
    {
        var result = await _client.CallToolAsync("query",
            new Dictionary<string, object?> { ["sql"] = "DELETE FROM Orders" }, cancellationToken: Ct);

        Assert.True(result.IsError);
        var message = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.Contains("DELETE is not allowed", message);
    }

    [Fact]
    public async Task Calling_ef_model_returns_entities_and_tables_from_the_compiled_app()
    {
        var model = await CallAsync("ef_model");

        Assert.Equal("Shop.ShopContext", model.GetProperty("context").GetString());
        Assert.Equal(["Customer", "Order", "OrderLine"],
            model.GetProperty("entities").EnumerateArray().Select(e => e.GetProperty("entity").GetString()!));
        Assert.Equal(3, model.GetProperty("tables").GetArrayLength());
    }

    [Fact]
    public async Task Calling_ef_drift_on_a_database_never_migrated_lists_every_migration_as_pending()
    {
        var report = await CallAsync("ef_drift");

        Assert.False(report.GetProperty("inSync").GetBoolean());
        var migrations = report.GetProperty("migrations");
        Assert.False(migrations.GetProperty("historyTableExists").GetBoolean());
        Assert.Equal(2, migrations.GetProperty("pending").GetArrayLength());
        Assert.Contains("__EFMigrationsHistory", report.GetProperty("summary").GetString());
    }

    [Fact]
    public void Unknown_provider_fails_fast_with_a_clear_message()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<NotSupportedException>(() =>
            services.AddEfLens(new EfLensOptions { Provider = "oracle", ConnectionString = "x" }));
        Assert.Contains("oracle", ex.Message);
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await _client.CallToolAsync(tool, arguments, cancellationToken: Ct);

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement;
    }
}
