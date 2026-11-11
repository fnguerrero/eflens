using Microsoft.Data.Sqlite;

namespace EfLens.Providers;

/// <summary>SQLite schema provider. Every connection is forced to read-only mode.</summary>
public sealed class SqliteSchemaProvider : ISchemaProvider
{
    private readonly string _connectionString;

    public SqliteSchemaProvider(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A SQLite connection string is required.", nameof(connectionString));

        // ReadOnly also means a missing file is an error instead of a new empty database.
        _connectionString = new SqliteConnectionStringBuilder(connectionString)
        {
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type
            FROM sqlite_schema
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'
            ORDER BY type, name
            """;

        var tables = new List<TableInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tables.Add(new TableInfo(Schema: null, Name: reader.GetString(0), Kind: reader.GetString(1)));

        return tables;
    }
}
