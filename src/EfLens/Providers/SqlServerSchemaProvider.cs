using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace EfLens.Providers;

/// <summary>
/// SQL Server provider. Schema calls only read catalog views; every value travels as a parameter and
/// object names are resolved with <c>QUOTENAME</c>/<c>OBJECT_ID</c>, never concatenated into SQL.
/// </summary>
public sealed class SqlServerSchemaProvider : IDatabaseProvider
{
    /// <summary>
    /// Three layers: ScriptDom validates a single plain SELECT, the query runs in a transaction that is always
    /// rolled back, and limits cut it short (rows stop being read and the server is told to cancel).
    /// The login should still be read-only (db_datareader): that is the layer EfLens cannot provide.
    /// </summary>
    public async Task<QueryResult> QueryAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default)
    {
        SqlServerQueryValidator.Validate(sql);

        var stopwatch = Stopwatch.StartNew();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limits.Timeout);

        await using var command = new SqlCommand(sql, connection, transaction)
        {
            CommandTimeout = (int)Math.Ceiling(limits.Timeout.TotalSeconds) + 5, // the token fires first; this is a backstop
        };

        try
        {
            await using var reader = await command.ExecuteReaderAsync(timeout.Token);
            var (columns, rows, truncated) = await QueryRows.ReadAsync(reader, limits, timeout.Token);

            // Disposing a reader drains the remaining rows; cancelling first stops the server instead.
            if (truncated)
                command.Cancel();

            return new QueryResult(columns, rows, truncated, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SqlException { Number: -2 }
                                   && !cancellationToken.IsCancellationRequested)
        {
            throw new QueryTimeoutException(limits.Timeout);
        }
        catch (SqlException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            // Cancelling a running command surfaces as "Operation cancelled by user", not as a timeout.
            throw new QueryTimeoutException(limits.Timeout);
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QueryFailedException(ex.Message);
        }
        finally
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            catch (Exception) when (connection.State != ConnectionState.Open || transaction.Connection is null)
            {
                // The server already ended the transaction (e.g. after a cancel); nothing to roll back.
            }
        }
    }

    /// <summary>
    /// The estimated plan from <c>SET SHOWPLAN_XML ON</c>: SQL Server compiles the statement and returns the plan
    /// instead of running it. The statement must still pass the same validator as <see cref="QueryAsync"/>.
    /// </summary>
    public async Task<QueryPlan> ExplainAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default)
    {
        SqlServerQueryValidator.Validate(sql);

        await using var connection = await OpenAsync(cancellationToken);
        var timeoutSeconds = (int)Math.Ceiling(limits.Timeout.TotalSeconds);
        try
        {
            // SHOWPLAN must be alone in its batch, so it is switched on and off around the statement.
            await new SqlCommand("SET SHOWPLAN_XML ON", connection).ExecuteNonQueryAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
            var xml = (string?)await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new QueryFailedException("SQL Server returned no plan.");
            return ShowPlan.Parse(xml);
        }
        catch (SqlException ex) when (ex.Number == -2 && !cancellationToken.IsCancellationRequested)
        {
            throw new QueryTimeoutException(limits.Timeout);
        }
        catch (SqlException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new QueryFailedException(ex.Message);
        }
    }

    private readonly string _connectionString;

    public SqlServerSchemaProvider(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A SQL Server connection string is required.", nameof(connectionString));

        // Declares intent (and routes to a readable secondary when there is one). Real enforcement is the
        // login: point EfLens at a user that only has db_datareader.
        _connectionString = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
        }.ConnectionString;
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT TABLE_SCHEMA, TABLE_NAME, CASE TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END AS kind
            FROM INFORMATION_SCHEMA.TABLES
            ORDER BY kind, TABLE_SCHEMA, TABLE_NAME
            """, connection);

        var tables = new List<TableInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            tables.Add(new TableInfo(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return tables;
    }

    public async Task<TableDetails> DescribeTableAsync(string table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var target = await ResolveAsync(connection, table, cancellationToken);

        var columns = new List<ColumnInfo>();
        await using (var command = new SqlCommand("""
            SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, dc.definition, ic.key_ordinal
            FROM sys.columns AS c
            JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
            LEFT JOIN sys.indexes AS pk ON pk.object_id = c.object_id AND pk.is_primary_key = 1
            LEFT JOIN sys.index_columns AS ic
                ON ic.object_id = pk.object_id AND ic.index_id = pk.index_id AND ic.column_id = c.column_id
            WHERE c.object_id = @id
            ORDER BY c.column_id
            """, connection))
        {
            command.Parameters.AddWithValue("@id", target.ObjectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new ColumnInfo(
                    Name: reader.GetString(0),
                    DataType: FormatType(reader.GetString(1), reader.GetInt16(2), reader.GetByte(3), reader.GetByte(4)),
                    IsNullable: reader.GetBoolean(5),
                    DefaultValue: reader.IsDBNull(6) ? null : reader.GetString(6),
                    PrimaryKeyOrdinal: reader.IsDBNull(7) ? null : reader.GetByte(7)));
            }
        }

        var indexes = new List<IndexInfo>();
        await using (var command = new SqlCommand("""
            SELECT i.name, i.is_unique, i.is_primary_key, c.name
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = @id AND i.type > 0 AND i.is_hypothetical = 0
            ORDER BY i.name, ic.key_ordinal
            """, connection))
        {
            command.Parameters.AddWithValue("@id", target.ObjectId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<(string Index, bool Unique, bool Primary, string Column)>();
            while (await reader.ReadAsync(cancellationToken))
                rows.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3)));

            indexes.AddRange(rows.GroupBy(r => r.Index).Select(g =>
                new IndexInfo(g.Key, g.Select(r => r.Column).ToList(), g.First().Unique, g.First().Primary)));
        }

        return new TableDetails(target.Schema, target.Name, target.Kind, columns, indexes);
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetRelationsAsync(string? table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        int? objectId = table is null ? null : (await ResolveAsync(connection, table, cancellationToken)).ObjectId;

        await using var command = new SqlCommand("""
            SELECT fk.name,
                   SCHEMA_NAME(fp.schema_id), fp.name, cp.name,
                   SCHEMA_NAME(rp.schema_id), rp.name, cr.name,
                   fk.delete_referential_action_desc
            FROM sys.foreign_keys AS fk
            JOIN sys.foreign_key_columns AS fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.tables AS fp ON fp.object_id = fk.parent_object_id
            JOIN sys.columns AS cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            JOIN sys.tables AS rp ON rp.object_id = fk.referenced_object_id
            JOIN sys.columns AS cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
            WHERE @id IS NULL OR fk.parent_object_id = @id OR fk.referenced_object_id = @id
            ORDER BY SCHEMA_NAME(fp.schema_id), fp.name, fk.name, fkc.constraint_column_id
            """, connection);
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = (object?)objectId ?? DBNull.Value });

        var rows = new List<(string Fk, string FromSchema, string FromTable, string FromColumn,
            string ToSchema, string ToTable, string ToColumn, string OnDelete)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7)));

        return rows
            .GroupBy(r => (r.FromSchema, r.Fk))
            .Select(g =>
            {
                var first = g.First();
                return new ForeignKeyInfo(
                    first.Fk,
                    first.FromSchema, first.FromTable, g.Select(r => r.FromColumn).ToList(),
                    first.ToSchema, first.ToTable, g.Select(r => r.ToColumn).ToList(),
                    first.OnDelete.Replace('_', ' '));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<string>?> GetAppliedMigrationsAsync(
        string? schema, string table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        // The name is quoted by the server (QUOTENAME), never concatenated on this side.
        await using var command = new SqlCommand("""
            DECLARE @name nvarchar(600) = QUOTENAME(ISNULL(@schema, SCHEMA_NAME())) + N'.' + QUOTENAME(@table);
            IF OBJECT_ID(@name, N'U') IS NULL
                SELECT CAST(0 AS bit);
            ELSE
            BEGIN
                SELECT CAST(1 AS bit);
                DECLARE @sql nvarchar(max) = N'SELECT MigrationId FROM ' + @name + N' ORDER BY MigrationId';
                EXEC sp_executesql @sql;
            END
            """, connection);
        command.Parameters.Add("@schema", SqlDbType.NVarChar, 128).Value = (object?)schema ?? DBNull.Value;
        command.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = table;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (!reader.GetBoolean(0))
            return null;

        await reader.NextResultAsync(cancellationToken);
        var applied = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            applied.Add(reader.GetString(0));
        return applied;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed record ResolvedTable(int ObjectId, string Schema, string Name, string Kind);

    /// <summary>Finds a table or view by <c>name</c> or <c>schema.name</c>; brackets are accepted.</summary>
    private static async Task<ResolvedTable> ResolveAsync(SqlConnection connection, string table, CancellationToken cancellationToken)
    {
        var (schema, name) = SplitName(table);

        await using var command = new SqlCommand("""
            SELECT OBJECT_ID(QUOTENAME(TABLE_SCHEMA) + N'.' + QUOTENAME(TABLE_NAME)),
                   TABLE_SCHEMA, TABLE_NAME, CASE TABLE_TYPE WHEN 'VIEW' THEN 'view' ELSE 'table' END
            FROM INFORMATION_SCHEMA.TABLES
            WHERE TABLE_NAME = @name AND (@schema IS NULL OR TABLE_SCHEMA = @schema)
            ORDER BY TABLE_SCHEMA
            """, connection);
        command.Parameters.Add(new SqlParameter("@name", System.Data.SqlDbType.NVarChar, 128) { Value = name });
        command.Parameters.Add(new SqlParameter("@schema", System.Data.SqlDbType.NVarChar, 128) { Value = (object?)schema ?? DBNull.Value });

        var matches = new List<ResolvedTable>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            matches.Add(new ResolvedTable(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new SchemaLookupException($"Table or view '{table}' was not found. Use list_tables to see what exists."),
            _ => throw new SchemaLookupException(
                $"'{table}' exists in more than one schema ({string.Join(", ", matches.Select(m => $"{m.Schema}.{m.Name}"))}). Qualify it with the schema."),
        };
    }

    internal static (string? Schema, string Name) SplitName(string table)
    {
        static string Unbracket(string part) => part.Trim().TrimStart('[').TrimEnd(']');

        var dot = table.IndexOf('.');
        return dot < 0
            ? (null, Unbracket(table))
            : (Unbracket(table[..dot]), Unbracket(table[(dot + 1)..]));
    }

    internal static string FormatType(string type, short maxLength, byte precision, byte scale) => type switch
    {
        "nvarchar" or "nchar" => $"{type}({(maxLength == -1 ? "max" : (maxLength / 2).ToString())})",
        "varchar" or "char" or "varbinary" or "binary" => $"{type}({(maxLength == -1 ? "max" : maxLength.ToString())})",
        "decimal" or "numeric" => $"{type}({precision},{scale})",
        "datetime2" or "datetimeoffset" or "time" => $"{type}({scale})",
        _ => type,
    };
}
