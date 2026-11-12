using System.ComponentModel;
using EfLens.Providers;
using ModelContextProtocol;
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

    [McpServerTool(Name = "describe_table", Title = "Describe table",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Columns (type, nullability, default, primary key position) and indexes of one table or view.")]
    public Task<TableDetails> DescribeTable(
        [Description("Table or view name. Qualify it with the schema (sales.Orders) when the name exists in more than one schema.")]
        string table,
        CancellationToken cancellationToken) =>
        ExposeLookupErrors(() => provider.DescribeTableAsync(table, cancellationToken));

    [McpServerTool(Name = "list_relations", Title = "List relations",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Foreign keys of the database, with their columns and ON DELETE action. Pass a table to get only the ones that start or end there.")]
    public Task<IReadOnlyList<ForeignKeyInfo>> ListRelations(
        [Description("Optional table name. When set, returns foreign keys from this table and the ones that reference it.")]
        string? table = null,
        CancellationToken cancellationToken = default) =>
        ExposeLookupErrors(() => provider.GetRelationsAsync(string.IsNullOrWhiteSpace(table) ? null : table, cancellationToken));

    /// <summary>
    /// Lookup errors are meant for the agent ("table not found, use list_tables"), so they travel as MCP errors.
    /// Anything else keeps the SDK's generic message and does not leak driver details.
    /// </summary>
    private static async Task<T> ExposeLookupErrors<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (SchemaLookupException ex)
        {
            throw new McpException(ex.Message);
        }
    }
}
