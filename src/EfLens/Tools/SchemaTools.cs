using System.ComponentModel;
using EfLens.Providers;
using ModelContextProtocol.Server;

namespace EfLens.Tools;

/// <summary>MCP tools that describe the database schema. All of them are read-only.</summary>
[McpServerToolType]
public sealed class SchemaTools(ISchemaProvider provider)
{
    [McpServerTool(Name = "list_tables", Title = "List tables",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists the tables and views of the connected database, ordered by kind and name. System tables are excluded.")]
    public Task<IReadOnlyList<TableInfo>> ListTables(CancellationToken cancellationToken) =>
        provider.ListTablesAsync(cancellationToken);
}
