namespace EfLens.Providers;

/// <summary>A table or view found in the database.</summary>
/// <param name="Schema">Schema name, or null when the provider has no schemas (SQLite).</param>
/// <param name="Name">Object name.</param>
/// <param name="Kind"><c>table</c> or <c>view</c>.</param>
public sealed record TableInfo(string? Schema, string Name, string Kind);

/// <summary>Read-only access to a database schema. One implementation per provider.</summary>
public interface ISchemaProvider
{
    Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken = default);
}
