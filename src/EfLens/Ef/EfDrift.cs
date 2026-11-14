using EfLens.Providers;

namespace EfLens.Ef;

/// <summary>Whether the real database still matches the app's EF Core model and migrations.</summary>
/// <param name="InSync">True when every migration is applied, none is unknown, and the schema matches the model.</param>
/// <param name="Summary">One or two sentences for the agent: what is wrong and what usually fixes it.</param>
public sealed record EfDriftReport(
    string Context,
    bool InSync,
    string Summary,
    EfMigrationStatus Migrations,
    IReadOnlyList<EfDifference> Differences);

/// <param name="HistoryTableExists">False when the database has no migrations history table at all.</param>
/// <param name="Pending">Migrations in the app that the database has not applied, oldest first.</param>
/// <param name="NotInAssembly">Migrations the database applied that this build of the app does not have.</param>
/// <param name="ModelChangedSinceLastMigration">The model changed after the last migration (null: unknown).</param>
public sealed record EfMigrationStatus(
    string HistoryTable,
    bool HistoryTableExists,
    IReadOnlyList<string> Applied,
    IReadOnlyList<string> Pending,
    IReadOnlyList<string> NotInAssembly,
    bool? ModelChangedSinceLastMigration);

/// <summary>One difference between what EF Core expects and what the database has.</summary>
/// <param name="Kind">
/// <c>missing_table</c>, <c>extra_table</c>, <c>missing_column</c>, <c>extra_column</c>, <c>type_mismatch</c>,
/// <c>nullability_mismatch</c>, <c>primary_key_mismatch</c>, <c>missing_foreign_key</c>, <c>extra_foreign_key</c>,
/// <c>on_delete_mismatch</c>, <c>missing_index</c>, <c>extra_index</c> or <c>index_uniqueness_mismatch</c>.
/// </param>
/// <param name="Table">Table, schema-qualified when it has a schema.</param>
/// <param name="Object">Column, key, foreign key or index involved, when there is one.</param>
/// <param name="Expected">What the EF Core model says.</param>
/// <param name="Actual">What the database has.</param>
public sealed record EfDifference(string Kind, string Table, string? Object, string? Expected, string? Actual);

/// <summary>A database table as the schema tools see it.</summary>
public sealed record DatabaseTable(TableDetails Details, IReadOnlyList<ForeignKeyInfo> ForeignKeys);

/// <summary>Reads the model and the database, then compares them.</summary>
public sealed class EfDriftService(IEfModelSource models, IDatabaseProvider database)
{
    public async Task<EfDriftReport> CheckAsync(string? contextName, CancellationToken cancellationToken = default)
    {
        var model = await models.GetModelAsync(contextName, cancellationToken);
        var applied = await database.GetAppliedMigrationsAsync(
            model.Migrations.HistorySchema, model.Migrations.HistoryTable, cancellationToken);

        var relations = await database.GetRelationsAsync(null, cancellationToken);
        var tables = new List<DatabaseTable>();
        foreach (var table in await database.ListTablesAsync(cancellationToken))
        {
            if (table.Kind != "table")
                continue;
            var details = await database.DescribeTableAsync(QualifiedName(table.Schema, table.Name), cancellationToken);
            tables.Add(new DatabaseTable(details, [.. relations.Where(r =>
                DriftDetector.SameTable(r.FromSchema, r.FromTable, table.Schema, table.Name))]));
        }

        return DriftDetector.Compare(model, applied, tables);
    }

    /// <summary>The exact name the schema tools resolve: plain without schemas (SQLite), bracketed with them.</summary>
    private static string QualifiedName(string? schema, string name) =>
        schema is null ? name : $"{Bracket(schema)}.{Bracket(name)}";

    private static string Bracket(string identifier) => $"[{identifier.Replace("]", "]]")}]";
}

/// <summary>The comparison itself: pure, so every rule is unit tested without a database.</summary>
public static class DriftDetector
{
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;

    public static EfDriftReport Compare(EfModelInfo model, IReadOnlyList<string>? applied, IReadOnlyList<DatabaseTable> database)
    {
        var migrations = CompareMigrations(model.Migrations, applied);
        var differences = new List<EfDifference>();

        var matched = new HashSet<DatabaseTable>();
        foreach (var expected in model.Tables)
        {
            var actual = database.FirstOrDefault(t => SameTable(expected.Schema, expected.Name, t.Details.Schema, t.Details.Name));
            var name = Display(expected.Schema, expected.Name);
            if (actual is null)
            {
                differences.Add(new("missing_table", name, null, "table", null));
                continue;
            }

            matched.Add(actual);
            CompareColumns(name, expected, actual.Details, differences);
            ComparePrimaryKey(name, expected, actual.Details, differences);
            CompareForeignKeys(name, expected, actual.ForeignKeys, differences);
            CompareIndexes(name, expected, actual.Details, differences);
        }

        foreach (var extra in database.Where(t => !matched.Contains(t) && !Names.Equals(t.Details.Name, model.Migrations.HistoryTable)))
            differences.Add(new("extra_table", Display(extra.Details.Schema, extra.Details.Name), null, null, "table"));

        var inSync = migrations.Pending.Count == 0
            && migrations.NotInAssembly.Count == 0
            && migrations.ModelChangedSinceLastMigration != true
            && differences.Count == 0;

        return new EfDriftReport(model.Context, inSync, Summarize(migrations, differences, inSync), migrations, differences);
    }

    private static EfMigrationStatus CompareMigrations(EfMigrationsInfo info, IReadOnlyList<string>? applied)
    {
        var done = applied ?? [];
        return new EfMigrationStatus(
            info.HistoryTable,
            applied is not null,
            done,
            [.. info.Defined.Where(m => !done.Contains(m, StringComparer.Ordinal))],
            [.. done.Where(m => !info.Defined.Contains(m, StringComparer.Ordinal))],
            info.HasPendingModelChanges);
    }

    private static void CompareColumns(string table, EfTableInfo expected, TableDetails actual, List<EfDifference> differences)
    {
        foreach (var column in expected.Columns)
        {
            var found = actual.Columns.FirstOrDefault(c => Names.Equals(c.Name, column.Name));
            if (found is null)
            {
                differences.Add(new("missing_column", table, column.Name, Describe(column.StoreType, column.IsNullable), null));
                continue;
            }

            if (NormalizeType(found.DataType) != NormalizeType(column.StoreType))
                differences.Add(new("type_mismatch", table, column.Name, column.StoreType, found.DataType));
            if (found.IsNullable != column.IsNullable)
                differences.Add(new("nullability_mismatch", table, column.Name, Nullability(column.IsNullable), Nullability(found.IsNullable)));
        }

        foreach (var extra in actual.Columns.Where(c => !expected.Columns.Any(e => Names.Equals(e.Name, c.Name))))
            differences.Add(new("extra_column", table, extra.Name, null, Describe(extra.DataType, extra.IsNullable)));
    }

    private static void ComparePrimaryKey(string table, EfTableInfo expected, TableDetails actual, List<EfDifference> differences)
    {
        if (expected.PrimaryKey is null)
            return; // keyless entity: nothing to expect

        var actualKey = actual.Columns.Where(c => c.PrimaryKeyOrdinal is not null).OrderBy(c => c.PrimaryKeyOrdinal).Select(c => c.Name).ToList();
        if (!actualKey.SequenceEqual(expected.PrimaryKey.Columns, Names))
            differences.Add(new("primary_key_mismatch", table, expected.PrimaryKey.Name,
                Columns(expected.PrimaryKey.Columns), actualKey.Count == 0 ? null : Columns(actualKey)));
    }

    private static void CompareForeignKeys(string table, EfTableInfo expected, IReadOnlyList<ForeignKeyInfo> actual, List<EfDifference> differences)
    {
        var matched = new HashSet<ForeignKeyInfo>();
        foreach (var fk in expected.ForeignKeys)
        {
            var found = actual.FirstOrDefault(a => !matched.Contains(a)
                && a.FromColumns.SequenceEqual(fk.Columns, Names)
                && SameTable(fk.PrincipalSchema, fk.PrincipalTable, a.ToSchema, a.ToTable));
            var target = $"{Columns(fk.Columns)} -> {Display(fk.PrincipalSchema, fk.PrincipalTable)}{Columns(fk.PrincipalColumns)}";
            if (found is null)
            {
                differences.Add(new("missing_foreign_key", table, fk.Name, target, null));
                continue;
            }

            matched.Add(found);
            if (OnDelete(found.OnDelete) != OnDelete(fk.OnDelete))
                differences.Add(new("on_delete_mismatch", table, fk.Name, fk.OnDelete, found.OnDelete));
        }

        foreach (var extra in actual.Where(a => !matched.Contains(a)))
            differences.Add(new("extra_foreign_key", table, extra.Name, null,
                $"{Columns(extra.FromColumns)} -> {Display(extra.ToSchema, extra.ToTable)}{Columns(extra.ToColumns)}"));
    }

    private static void CompareIndexes(string table, EfTableInfo expected, TableDetails actual, List<EfDifference> differences)
    {
        // Unique constraints (alternate keys) are unique indexes on the database side.
        var wanted = expected.Indexes
            .Select(i => (i.Name, i.Columns, i.IsUnique))
            .Concat(expected.UniqueConstraints.Select(u => (u.Name, u.Columns, IsUnique: true)));
        var candidates = actual.Indexes.Where(i => !i.IsPrimaryKey).ToList();
        var matched = new HashSet<IndexInfo>();

        foreach (var index in wanted)
        {
            var found = candidates.FirstOrDefault(c => !matched.Contains(c) && c.Columns.SequenceEqual(index.Columns, Names));
            if (found is null)
            {
                differences.Add(new("missing_index", table, index.Name, Index(index.Columns, index.IsUnique), null));
                continue;
            }

            matched.Add(found);
            if (found.IsUnique != index.IsUnique)
                differences.Add(new("index_uniqueness_mismatch", table, index.Name,
                    Index(index.Columns, index.IsUnique), Index(found.Columns, found.IsUnique)));
        }

        foreach (var extra in candidates.Where(c => !matched.Contains(c)))
            differences.Add(new("extra_index", table, extra.Name, null, Index(extra.Columns, extra.IsUnique)));
    }

    private static string Summarize(EfMigrationStatus migrations, List<EfDifference> differences, bool inSync)
    {
        if (inSync)
            return $"In sync: {Count(migrations.Applied.Count, "migration")} applied and the schema matches the model.";

        var problems = new List<string>();
        if (!migrations.HistoryTableExists)
            problems.Add($"there is no {migrations.HistoryTable} table, so no migration was ever applied with EF Core");
        else if (migrations.Pending.Count > 0)
            problems.Add($"{Count(migrations.Pending.Count, "pending migration")} ({string.Join(", ", migrations.Pending)})");
        if (migrations.NotInAssembly.Count > 0)
            problems.Add($"{Count(migrations.NotInAssembly.Count, "applied migration")} missing from this build ({string.Join(", ", migrations.NotInAssembly)})");
        if (migrations.ModelChangedSinceLastMigration == true)
            problems.Add("model changes with no migration yet (run `dotnet ef migrations add`)");
        if (differences.Count > 0)
            problems.Add(Count(differences.Count, "schema difference"));

        var summary = $"Out of sync: {string.Join("; ", problems)}.";
        if (migrations.Pending.Count > 0 && differences.Count > 0)
            summary += " Pending migrations usually explain part of the differences: apply them (`dotnet ef database update`) and run ef_drift again; whatever remains was changed by hand.";
        return summary;
    }

    /// <summary>
    /// Compares table names ignoring case. A missing schema matches the usual default schemas (dbo, public, main), so
    /// EF Core's "default schema" (null) lines up with what the database reports.
    /// </summary>
    internal static bool SameTable(string? schemaA, string nameA, string? schemaB, string nameB) =>
        Names.Equals(nameA, nameB) && Names.Equals(DefaultSchema(schemaA), DefaultSchema(schemaB));

    private static string DefaultSchema(string? schema) =>
        schema is null || Names.Equals(schema, "dbo") || Names.Equals(schema, "public") || Names.Equals(schema, "main") ? "" : schema;

    /// <summary>
    /// Puts EF Core store types and catalog types in the same spelling: datetime2 = datetime2(7) on SQL Server,
    /// varchar = character varying and timestamp = timestamp without time zone on PostgreSQL, and so on.
    /// </summary>
    internal static string NormalizeType(string type)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(type.Trim().ToLowerInvariant(), @"\s+", " ")
            .Replace("character varying", "varchar", StringComparison.Ordinal)
            .Replace(" without time zone", "", StringComparison.Ordinal)
            .Replace("timestamptz", "timestamp with time zone", StringComparison.Ordinal)
            .Replace("double precision", "float", StringComparison.Ordinal);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s*([(),])\s*", "$1");
        return t switch
        {
            "datetime2" or "datetimeoffset" or "time" => $"{t}(7)",
            "rowversion" => "timestamp",
            "float(53)" => "float",
            "float(24)" => "real",
            "decimal" or "numeric" => "decimal(18,0)",
            _ when t.StartsWith("numeric(", StringComparison.Ordinal) => $"decimal{t["numeric".Length..]}",
            "integer" or "int4" => "int",
            "int8" => "bigint",
            "int2" => "smallint",
            "bool" => "boolean",
            "float8" => "float",
            "float4" => "real",
            _ => t,
        };
    }

    /// <summary>SQL Server and SQLite treat RESTRICT and NO ACTION the same way for an immediate check.</summary>
    private static string OnDelete(string action) =>
        action.Equals("RESTRICT", StringComparison.OrdinalIgnoreCase) ? "NO ACTION" : action.ToUpperInvariant();

    private static string Display(string? schema, string name) => schema is null ? name : $"{schema}.{name}";

    private static string Describe(string type, bool nullable) => $"{type} {Nullability(nullable)}";

    private static string Nullability(bool nullable) => nullable ? "NULL" : "NOT NULL";

    private static string Columns(IEnumerable<string> columns) => $"({string.Join(", ", columns)})";

    private static string Index(IEnumerable<string> columns, bool unique) => (unique ? "UNIQUE " : "") + Columns(columns);

    private static string Count(int count, string noun) => $"{count} {noun}{(count == 1 ? "" : "s")}";
}
