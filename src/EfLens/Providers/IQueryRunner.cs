using System.Data.Common;

namespace EfLens.Providers;

/// <summary>Caps applied to every query.</summary>
/// <param name="MaxRows">Rows returned at most; the rest is not read.</param>
/// <param name="Timeout">Wall-clock limit; the query is cancelled in the engine when it is reached.</param>
/// <param name="MaxCellLength">Longer text values are cut, so one huge column cannot flood the agent's context.</param>
public sealed record QueryLimits(int MaxRows, TimeSpan Timeout, int MaxCellLength = 500);

/// <summary>Rows as arrays, in column order: compact for the agent to read.</summary>
/// <param name="Truncated">True when the query had more rows than <see cref="QueryLimits.MaxRows"/>.</param>
public sealed record QueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<object?[]> Rows,
    bool Truncated,
    long ElapsedMs);

/// <summary>Runs a single read-only SELECT under <see cref="QueryLimits"/>.</summary>
public interface IQueryRunner
{
    /// <exception cref="QueryRejectedException">The SQL is not a single read-only SELECT.</exception>
    /// <exception cref="QueryFailedException">The engine refused it (unknown column, syntax error...).</exception>
    /// <exception cref="QueryTimeoutException">It ran past <see cref="QueryLimits.Timeout"/>.</exception>
    Task<QueryResult> QueryAsync(string sql, QueryLimits limits, CancellationToken cancellationToken = default);
}

/// <summary>Reads the migrations EF Core recorded as applied.</summary>
public interface IMigrationHistory
{
    /// <param name="schema">Schema of the history table, or null for the connection's default schema.</param>
    /// <param name="table">History table name, usually <c>__EFMigrationsHistory</c>.</param>
    /// <returns>Applied migration ids in order, or null when the history table does not exist.</returns>
    Task<IReadOnlyList<string>?> GetAppliedMigrationsAsync(string? schema, string table, CancellationToken cancellationToken = default);
}

/// <summary>Everything a database provider offers.</summary>
public interface IDatabaseProvider : ISchemaProvider, IQueryRunner, IMigrationHistory;

internal static class QueryRows
{
    /// <summary>Reads at most <c>MaxRows + 1</c> rows: the extra one only tells whether the result was truncated.</summary>
    public static async Task<(List<string> Columns, List<object?[]> Rows, bool Truncated)> ReadAsync(
        DbDataReader reader, QueryLimits limits, CancellationToken cancellationToken)
    {
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count == limits.MaxRows)
                return (columns, rows, true);

            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i), limits.MaxCellLength);
            rows.Add(row);
        }
        return (columns, rows, false);
    }

    /// <summary>Makes every value JSON-safe and bounded in size.</summary>
    internal static object? Normalize(object? value, int maxCellLength) => value switch
    {
        null => null,
        string s when s.Length > maxCellLength => $"{s[..maxCellLength]}… ({s.Length} chars)",
        string s => s,
        byte[] b when b.Length > 32 => $"0x{Convert.ToHexString(b, 0, 32)}… ({b.Length} bytes)",
        byte[] b => $"0x{Convert.ToHexString(b)}",
        double d when !double.IsFinite(d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        float f when !float.IsFinite(f) => f.ToString(System.Globalization.CultureInfo.InvariantCulture),
        bool or byte or short or int or long or float or double or decimal
            or Guid or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly => value,
        _ => value.ToString(),
    };
}
