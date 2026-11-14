using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace EfLens.Providers;

/// <summary>
/// PostgreSQL provider. Schema calls only read <c>pg_catalog</c> with positional parameters. Npgsql runs in raw
/// mode (no client-side SQL rewriting), so the server receives the text exactly as sent and refuses more than
/// one statement in it.
/// </summary>
public sealed class PostgresSchemaProvider : IDatabaseProvider
{
    /// <summary>
    /// Must be set before Npgsql's first command: with rewriting on, Npgsql splits <c>SELECT 1; COMMIT; DELETE ...</c>
    /// into a batch on the client; with it off, the server's extended protocol rejects any second statement.
    /// </summary>
    internal const string RawModeSwitch = "Npgsql.EnableSqlRewriting";

    private readonly string _connectionString;
    private readonly string _queryConnectionString;

    public PostgresSchemaProvider(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));
        if (!AppContext.TryGetSwitch(RawModeSwitch, out var rewriting) || rewriting)
            throw new InvalidOperationException($"Set the AppContext switch {RawModeSwitch}=false before using Npgsql.");

        // Every session starts read-only too, so even schema calls cannot write by accident.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "EfLens" };
        builder.Options = string.Join(' ', new[] { builder.Options, "-c default_transaction_read_only=on" }.Where(o => !string.IsNullOrWhiteSpace(o)));
        _connectionString = builder.ToString();

        // Query connections end with session state an agent may have touched; they are never pooled.
        builder.Pooling = false;
        _queryConnectionString = builder.ToString();
    }

    // ---- query and explain ---------------------------------------------------------------------------------

    /// <summary>
    /// Five layers: one statement only (enforced by the server); PostgreSQL plans it first and a plan that writes or
    /// locks rows is rejected; it runs inside a READ ONLY transaction that is always rolled back (the engine refuses
    /// writes even inside functions); it is read through a cursor, which only accepts a SELECT or VALUES and makes
    /// the server produce no more rows than the cap; and <c>statement_timeout</c> stops it in the server.
    /// </summary>
    public async Task<QueryResult> QueryAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        return await RunGuardedAsync(sql, limits, async (connection, _, token) =>
        {
            await ExecuteAsync(connection, $"DECLARE eflens_query NO SCROLL CURSOR FOR {sql}", token);

            // One row more than the cap tells whether the result was truncated; the server computes nothing beyond it.
            var fetch = (limits.MaxRows + 1).ToString(CultureInfo.InvariantCulture);
            await using var command = new NpgsqlCommand($"FETCH FORWARD {fetch} FROM eflens_query", connection)
            {
                CommandTimeout = (int)Math.Ceiling(limits.Timeout.TotalSeconds) + 5, // the server timeout fires first
            };
            await using var reader = await command.ExecuteReaderAsync(token);
            var (columns, rows, truncated) = await QueryRows.ReadAsync(reader, limits, token);
            return new QueryResult(columns, rows, truncated, stopwatch.ElapsedMilliseconds);
        }, cancellationToken);
    }

    /// <summary>EXPLAIN without ANALYZE: PostgreSQL plans the statement and never runs it.</summary>
    public Task<QueryPlan> ExplainAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default) =>
        RunGuardedAsync(sql, limits, (_, plan, _) => Task.FromResult(plan), cancellationToken);

    private async Task<T> RunGuardedAsync<T>(
        string sql, QueryLimits limits, Func<NpgsqlConnection, QueryPlan, CancellationToken, Task<T>> run,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new QueryRejectedException("the query is empty.");

        await using var connection = new NpgsqlConnection(_queryConnectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            var timeoutMs = ((long)limits.Timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
            await ExecuteAsync(connection, "START TRANSACTION ISOLATION LEVEL READ COMMITTED READ ONLY", cancellationToken);
            await ExecuteAsync(connection, $"SET LOCAL statement_timeout = {timeoutMs}", cancellationToken);

            // Only plannable statements get a plan: SET, COMMIT, DO, CALL, COPY... fail here, before running.
            QueryPlan plan;
            await using (var explain = new NpgsqlCommand($"EXPLAIN (FORMAT JSON, VERBOSE) {sql}", connection))
                plan = ParsePlan((string)(await explain.ExecuteScalarAsync(cancellationToken))!);

            return await run(connection, plan, cancellationToken);
        }
        catch (PostgresException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw ex.SqlState switch
            {
                PostgresErrorCodes.QueryCanceled => new QueryTimeoutException(limits.Timeout),
                PostgresErrorCodes.ReadOnlySqlTransaction => new QueryRejectedException($"the database refused to write ({ex.MessageText})."),
                PostgresErrorCodes.SyntaxError when ex.MessageText.Contains("multiple commands", StringComparison.Ordinal)
                    => new QueryRejectedException("found more than one statement; send exactly one."),
                PostgresErrorCodes.SyntaxError => new QueryRejectedException($"{ex.MessageText}."),
                _ => new QueryFailedException(ex.MessageText),
            };
        }
        finally
        {
            // Pooling is off, so closing would end the transaction anyway; rolling back says it explicitly.
            try
            {
                if (connection.State == ConnectionState.Open)
                    await ExecuteAsync(connection, "ROLLBACK", CancellationToken.None);
            }
            catch (NpgsqlException)
            {
                // The connection is broken; the server discards the transaction with it.
            }
        }
    }

    /// <summary>Reads the JSON plan and rejects writes and row locks before anything runs.</summary>
    internal static QueryPlan ParsePlan(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement[0].GetProperty("Plan");
        var notes = new List<string>();

        PlanNode Node(JsonElement node)
        {
            var type = node.GetProperty("Node Type").GetString()!;
            if (type == "ModifyTable")
                throw new QueryRejectedException($"{node.GetProperty("Operation").GetString()!.ToUpperInvariant()} is not allowed.");
            if (type == "LockRows")
                throw new QueryRejectedException("row locks (FOR UPDATE / FOR SHARE) are not allowed.");

            string? target = null;
            if (node.TryGetProperty("Relation Name", out var relation))
            {
                target = node.TryGetProperty("Schema", out var schema) ? $"{schema.GetString()}.{relation.GetString()}" : relation.GetString();
                if (type == "Seq Scan" && node.TryGetProperty("Filter", out var filter))
                    notes.Add($"Seq Scan on {target} filtered by {filter.GetString()}: an index on the filtered columns may help.");
            }
            if (node.TryGetProperty("Index Name", out var index))
                target = target is null ? index.GetString() : $"{target} ({index.GetString()})";

            if (node.TryGetProperty("Join Type", out var join) && type.EndsWith("Join", StringComparison.Ordinal))
                type = $"{type} ({join.GetString()})";

            return new PlanNode(
                type,
                target,
                node.TryGetProperty("Plan Rows", out var rows) ? rows.GetDouble() : null,
                node.TryGetProperty("Total Cost", out var cost) ? cost.GetDouble() : null,
                node.TryGetProperty("Plans", out var children) ? [.. children.EnumerateArray().Select(Node)] : []);
        }

        var tree = Node(root);
        return new QueryPlan(tree.EstimatedCost, [tree], notes);
    }

    // ---- schema --------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT n.nspname, c.relname, CASE WHEN c.relkind IN ('v', 'm') THEN 'view' ELSE 'table' END
            FROM pg_catalog.pg_class AS c
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm') AND NOT c.relispartition
              AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
            ORDER BY CASE WHEN c.relkind IN ('v', 'm') THEN 1 ELSE 0 END, n.nspname, c.relname
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
        await using (var command = new NpgsqlCommand("""
            SELECT a.attname, pg_catalog.format_type(a.atttypid, a.atttypmod), NOT a.attnotnull,
                   pg_catalog.pg_get_expr(d.adbin, d.adrelid), pg_catalog.array_position(pk.conkey, a.attnum)
            FROM pg_catalog.pg_attribute AS a
            LEFT JOIN pg_catalog.pg_attrdef AS d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            LEFT JOIN pg_catalog.pg_constraint AS pk ON pk.conrelid = a.attrelid AND pk.contype = 'p'
            WHERE a.attrelid = $1 AND a.attnum > 0 AND NOT a.attisdropped
            ORDER BY a.attnum
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = target.Oid, NpgsqlDbType = NpgsqlDbType.Oid });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                columns.Add(new ColumnInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4)));
        }

        var indexes = new List<IndexInfo>();
        await using (var command = new NpgsqlCommand("""
            SELECT i.relname, x.indisunique, x.indisprimary,
                   ARRAY(SELECT a.attname::text
                         FROM unnest(x.indkey::int2[]) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_catalog.pg_attribute AS a ON a.attrelid = x.indrelid AND a.attnum = k.attnum
                         WHERE k.ord <= x.indnkeyatts
                         ORDER BY k.ord)
            FROM pg_catalog.pg_index AS x
            JOIN pg_catalog.pg_class AS i ON i.oid = x.indexrelid
            WHERE x.indrelid = $1
            ORDER BY i.relname
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter { Value = target.Oid, NpgsqlDbType = NpgsqlDbType.Oid });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                indexes.Add(new IndexInfo(reader.GetString(0), reader.GetFieldValue<string[]>(3), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return new TableDetails(target.Schema, target.Name, target.Kind, columns, indexes);
    }

    public async Task<IReadOnlyList<ForeignKeyInfo>> GetRelationsAsync(string? table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        uint? oid = table is null ? null : (await ResolveAsync(connection, table, cancellationToken)).Oid;

        await using var command = new NpgsqlCommand("""
            SELECT c.conname, fn.nspname, fc.relname,
                   ARRAY(SELECT a.attname::text FROM unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_catalog.pg_attribute AS a ON a.attrelid = c.conrelid AND a.attnum = k.attnum ORDER BY k.ord),
                   tn.nspname, tc.relname,
                   ARRAY(SELECT a.attname::text FROM unnest(c.confkey) WITH ORDINALITY AS k(attnum, ord)
                         JOIN pg_catalog.pg_attribute AS a ON a.attrelid = c.confrelid AND a.attnum = k.attnum ORDER BY k.ord),
                   CASE c.confdeltype WHEN 'c' THEN 'CASCADE' WHEN 'n' THEN 'SET NULL' WHEN 'd' THEN 'SET DEFAULT'
                                      WHEN 'r' THEN 'RESTRICT' ELSE 'NO ACTION' END
            FROM pg_catalog.pg_constraint AS c
            JOIN pg_catalog.pg_class AS fc ON fc.oid = c.conrelid
            JOIN pg_catalog.pg_namespace AS fn ON fn.oid = fc.relnamespace
            JOIN pg_catalog.pg_class AS tc ON tc.oid = c.confrelid
            JOIN pg_catalog.pg_namespace AS tn ON tn.oid = tc.relnamespace
            WHERE c.contype = 'f' AND ($1::oid IS NULL OR c.conrelid = $1 OR c.confrelid = $1)
            ORDER BY fn.nspname, fc.relname, c.conname
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)oid ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Oid });

        var relations = new List<ForeignKeyInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            relations.Add(new ForeignKeyInfo(
                reader.GetString(0),
                reader.GetString(1), reader.GetString(2), reader.GetFieldValue<string[]>(3),
                reader.GetString(4), reader.GetString(5), reader.GetFieldValue<string[]>(6),
                reader.GetString(7)));
        return relations;
    }

    public async Task<IReadOnlyList<string>?> GetAppliedMigrationsAsync(
        string? schema, string table, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);

        // The server quotes the identifiers (format %I); the text built there is the only SQL that comes back.
        string? sql;
        await using (var build = new NpgsqlCommand("""
            SELECT format('SELECT "MigrationId" FROM %I.%I ORDER BY 1', s, $2)
            FROM (SELECT coalesce($1, current_schema()) AS s) AS target
            WHERE to_regclass(format('%I.%I', s, $2)) IS NOT NULL
            """, connection))
        {
            build.Parameters.Add(new NpgsqlParameter { Value = (object?)schema ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            build.Parameters.Add(new NpgsqlParameter { Value = table, NpgsqlDbType = NpgsqlDbType.Text });
            sql = (string?)await build.ExecuteScalarAsync(cancellationToken);
        }
        if (sql is null)
            return null;

        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var applied = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
            applied.Add(reader.GetString(0));
        return applied;
    }

    // ---- helpers -------------------------------------------------------------------------------------------

    private async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ResolvedTable(uint Oid, string Schema, string Name, string Kind);

    /// <summary>
    /// Exact (quoted-style) match first, then case-insensitive: <c>orders</c> finds <c>"Orders"</c> created by EF Core,
    /// as long as only one table answers to it.
    /// </summary>
    private static async Task<ResolvedTable> ResolveAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        var (schema, name) = SplitName(table);

        await using var command = new NpgsqlCommand("""
            SELECT c.oid, n.nspname, c.relname, CASE WHEN c.relkind IN ('v', 'm') THEN 'view' ELSE 'table' END,
                   c.relname = $2 AND ($1::text IS NULL OR n.nspname = $1)
            FROM pg_catalog.pg_class AS c
            JOIN pg_catalog.pg_namespace AS n ON n.oid = c.relnamespace
            WHERE c.relkind IN ('r', 'p', 'v', 'm')
              AND lower(c.relname) = lower($2) AND ($1::text IS NULL OR lower(n.nspname) = lower($1))
              AND n.nspname <> 'information_schema' AND n.nspname NOT LIKE 'pg\_%'
            ORDER BY n.nspname, c.relname
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)schema ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = name, NpgsqlDbType = NpgsqlDbType.Text });

        var matches = new List<(ResolvedTable Table, bool Exact)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                matches.Add((new ResolvedTable(reader.GetFieldValue<uint>(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                    reader.GetBoolean(4)));
        }

        if (matches.Count(m => m.Exact) == 1)
            return matches.Single(m => m.Exact).Table;

        return matches.Count switch
        {
            1 => matches[0].Table,
            0 => throw new SchemaLookupException($"Table or view '{table}' was not found. Use list_tables to see what exists."),
            _ => throw new SchemaLookupException(
                $"'{table}' exists in more than one schema ({string.Join(", ", matches.Select(m => $"{m.Table.Schema}.{m.Table.Name}"))}). Qualify it with the schema."),
        };
    }

    /// <summary>Accepts <c>orders</c>, <c>sales.orders</c>, <c>"sales"."Orders"</c> (and brackets, for habit).</summary>
    internal static (string? Schema, string Name) SplitName(string table)
    {
        static string Unquote(string part) => part.Trim().Trim('"', '[', ']');

        var dot = table.IndexOf('.');
        return dot < 0
            ? (null, Unquote(table))
            : (Unquote(table[..dot]), Unquote(table[(dot + 1)..]));
    }
}

/// <summary>Turns off Npgsql's client-side SQL rewriting before any Npgsql type is used in this process.</summary>
internal static class NpgsqlRawMode
{
    [ModuleInitializer]
    internal static void Enable() => AppContext.SetSwitch(PostgresSchemaProvider.RawModeSwitch, false);
}
