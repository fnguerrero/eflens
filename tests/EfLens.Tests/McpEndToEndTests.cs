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

    public async Task InitializeAsync()
    {
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddEfLens(new EfLensOptions { Provider = "sqlite", ConnectionString = _db.ConnectionString });
        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream())
            .WithEfLensTools();

        _host = builder.Build();
        await _host.StartAsync();

        _client = await McpClient.CreateAsync(
            new StreamClientTransport(_clientToServer.Writer.AsStream(), _serverToClient.Reader.AsStream(), loggerFactory: null));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task Server_advertises_list_tables_as_a_read_only_tool()
    {
        var tools = await _client.ListToolsAsync();

        var tool = Assert.Single(tools, t => t.Name == "list_tables");
        Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint);
    }

    [Fact]
    public async Task Calling_list_tables_returns_the_schema_as_json()
    {
        var result = await _client.CallToolAsync("list_tables");

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        var names = JsonDocument.Parse(text).RootElement.EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["Customers", "Orders", "CustomerTotals"], names);
    }

    [Fact]
    public void Unknown_provider_fails_fast_with_a_clear_message()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<NotSupportedException>(() =>
            services.AddEfLens(new EfLensOptions { Provider = "oracle", ConnectionString = "x" }));
        Assert.Contains("oracle", ex.Message);
    }
}
