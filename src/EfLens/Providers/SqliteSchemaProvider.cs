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
        await using var connection = await OpenAsync(cancellationToken);
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

    public async Task<TableDetails> DescribeTableAsync(string table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var (name, kind) = await ResolveAsync(connection, table, cancellationToken);

        var columns = new List<ColumnInfo>();
        await using (var command = connection.CreateCommand())
        {
            // Table-valued pragmas take bound parameters, so the name never becomes SQL text.
            command.CommandText = """SELECT name, type, "notnull", dflt_value, pk FROM pragma_table_info($table) ORDER BY cid""";
            command.Parameters.AddWithValue("$table", name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var pk = reader.GetInt32(4);
                columns.Add(new ColumnInfo(
                    Name: reader.GetString(0),
                    DataType: reader.GetString(1),
                    IsNullable: reader.GetInt32(2) == 0 && pk == 0,
                    DefaultValue: reader.IsDBNull(3) ? null : reader.GetString(3),
                    PrimaryKeyOrdinal: pk == 0 ? null : pk));
            }
        }

        var indexes = new List<IndexInfo>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT il.name, il."unique", il.origin, ii.name
                FROM pragma_index_list($table) AS il
                JOIN pragma_index_info(il.name) AS ii
                ORDER BY il.name, ii.seqno
                """;
            command.Parameters.AddWithValue("$table", name);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            string? current = null;
            List<string> indexColumns = [];
            bool unique = false, primary = false;
            while (await reader.ReadAsync(cancellationToken))
            {
                var indexName = reader.GetString(0);
                if (indexName != current)
                {
                    if (current is not null) indexes.Add(new IndexInfo(current, indexColumns, unique, primary));
                    current = indexName;
                    indexColumns = [];
                    unique = reader.GetInt32(1) == 1;
                    primary = reader.GetString(2) == "pk";
                }
                indexColumns.Add(reader.GetString(3));
            }
            if (current is not null) indexes.Add(new IndexInfo(current, indexColumns, unique, primary));
        }

        return new TableDetails(Schema: null, name, kind, columns, indexes);
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetRelationsAsync(string? table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        string? filter = table is null ? null : (await ResolveAsync(connection, table, cancellationToken)).Name;

        var rows = new List<(string From, int Id, string To, string FromColumn, string? ToColumn, string OnDelete)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT m.name, fk.id, fk."table", fk."from", fk."to", fk.on_delete
                FROM sqlite_schema AS m
                JOIN pragma_foreign_key_list(m.name) AS fk
                WHERE m.type = 'table'
                ORDER BY m.name, fk.id, fk.seq
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5)));
        }

        var relations = new List<ForeignKeyInfo>();
        foreach (var fk in rows.GroupBy(r => (r.From, r.Id)))
        {
            var first = fk.First();
            if (filter is not null
                && !first.From.Equals(filter, StringComparison.OrdinalIgnoreCase)
                && !first.To.Equals(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            // "REFERENCES Customers" without columns points at the target's primary key.
            var toColumns = fk.All(r => r.ToColumn is not null)
                ? fk.Select(r => r.ToColumn!).ToList()
                : await PrimaryKeyColumnsAsync(connection, first.To, cancellationToken);

            relations.Add(new ForeignKeyInfo(
                Name: null,
                FromSchema: null, FromTable: first.From, FromColumns: fk.Select(r => r.FromColumn).ToList(),
                ToSchema: null, ToTable: first.To, ToColumns: toColumns,
                OnDelete: first.OnDelete));
        }

        return relations;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<(string Name, string Kind)> ResolveAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, type FROM sqlite_schema
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%' AND name = $name COLLATE NOCASE
            """;
        command.Parameters.AddWithValue("$name", table.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new SchemaLookupException($"Table or view '{table}' was not found. Use list_tables to see what exists.");
        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<List<string>> PrimaryKeyColumnsAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table) WHERE pk > 0 ORDER BY pk";
        command.Parameters.AddWithValue("$table", table);
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.Add(reader.GetString(0));
        return columns;
    }
}
