namespace EfLens.Providers;

/// <summary>A table or view found in the database.</summary>
/// <param name="Schema">Schema name, or null when the provider has no schemas (SQLite).</param>
/// <param name="Name">Object name.</param>
/// <param name="Kind"><c>table</c> or <c>view</c>.</param>
public sealed record TableInfo(string? Schema, string Name, string Kind);

/// <summary>A column of a table or view.</summary>
/// <param name="DataType">Type as the database declares it, e.g. <c>nvarchar(100)</c> or <c>decimal(10,2)</c>.</param>
/// <param name="PrimaryKeyOrdinal">1-based position inside the primary key, or null when the column is not part of it.</param>
public sealed record ColumnInfo(string Name, string DataType, bool IsNullable, string? DefaultValue, int? PrimaryKeyOrdinal);

/// <summary>An index, including the ones that back primary keys and unique constraints.</summary>
public sealed record IndexInfo(string Name, IReadOnlyList<string> Columns, bool IsUnique, bool IsPrimaryKey);

/// <summary>Columns and indexes of one table or view.</summary>
public sealed record TableDetails(
    string? Schema, string Name, string Kind,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IndexInfo> Indexes);

/// <summary>A foreign key between two tables.</summary>
/// <param name="Name">Constraint name, or null when the database does not name it (SQLite).</param>
/// <param name="OnDelete">Referential action, e.g. <c>NO ACTION</c>, <c>CASCADE</c>, <c>SET NULL</c>.</param>
public sealed record ForeignKeyInfo(
    string? Name,
    string? FromSchema, string FromTable, IReadOnlyList<string> FromColumns,
    string? ToSchema, string ToTable, IReadOnlyList<string> ToColumns,
    string OnDelete);

/// <summary>Read-only access to a database schema. One implementation per provider.</summary>
public interface ISchemaProvider
{
    Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken = default);

    /// <param name="table">Table or view name, optionally schema-qualified (<c>sales.Orders</c>).</param>
    /// <exception cref="SchemaLookupException">The table does not exist or the name is ambiguous.</exception>
    Task<TableDetails> DescribeTableAsync(string table, CancellationToken cancellationToken = default);

    /// <param name="table">
    /// When set, only foreign keys that start or end at this table. When null, every foreign key in the database.
    /// </param>
    /// <exception cref="SchemaLookupException">The table does not exist or the name is ambiguous.</exception>
    Task<IReadOnlyList<ForeignKeyInfo>> GetRelationsAsync(string? table, CancellationToken cancellationToken = default);
}

/// <summary>A table name that could not be resolved. The message is safe to show to the MCP client.</summary>
public sealed class SchemaLookupException(string message) : Exception(message);
