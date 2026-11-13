using System.ComponentModel;
using EfLens.Providers;
using ModelContextProtocol.Server;

namespace EfLens.Tools;

/// <summary>The one tool that runs SQL written by the agent.</summary>
[McpServerToolType]
public sealed class QueryTools(IQueryRunner runner, EfLensOptions options)
{
    private const int DefaultRows = 50;

    [McpServerTool(Name = "query", Title = "Run a read-only query",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Runs ONE read-only SELECT (CTEs allowed) and returns the column names and the rows as arrays.
        Writes, schema changes, multiple statements, transactions, PRAGMA/ATTACH (SQLite) and linked servers or
        OPENROWSET (SQL Server) are rejected with the reason. Rows are capped: "truncated": true means there was more,
        so filter or aggregate instead of raising maxRows. Long text and binary values are shortened.
        Use list_tables and describe_table first to get exact names.
        """)]
    public Task<QueryResult> Query(
        [Description("A single SELECT statement in the database's own SQL dialect.")]
        string sql,
        [Description("Rows to return, 1 to the server's configured maximum. Default 50.")]
        int? maxRows = null,
        CancellationToken cancellationToken = default)
    {
        var cap = Math.Max(1, options.MaxRows);
        var limits = new QueryLimits(
            MaxRows: Math.Clamp(maxRows ?? Math.Min(DefaultRows, cap), 1, cap),
            Timeout: TimeSpan.FromSeconds(Math.Max(1, options.QueryTimeoutSeconds)));

        return ToolErrors.Expose(() => runner.QueryAsync(sql, limits, cancellationToken));
    }
}
