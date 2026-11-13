namespace EfLens.Providers;

/// <summary>
/// An error whose message is written for the agent: it says what went wrong and how to recover, and never includes
/// connection details. Tools turn it into an MCP tool error; any other exception keeps the SDK's generic message.
/// </summary>
public abstract class EfLensException(string message) : Exception(message);

/// <summary>A table name that does not exist or exists in more than one schema.</summary>
public sealed class SchemaLookupException(string message) : EfLensException(message);

/// <summary>The SQL was refused before touching data: not a single read-only SELECT.</summary>
public sealed class QueryRejectedException(string reason)
    : EfLensException($"Query rejected: {reason} EfLens only runs one read-only SELECT statement.");

/// <summary>The database refused a valid read-only query, e.g. an unknown column. The message comes from the engine.</summary>
public sealed class QueryFailedException(string message) : EfLensException($"Query failed: {message}");

/// <summary>The query ran longer than the configured timeout and was cancelled.</summary>
public sealed class QueryTimeoutException(TimeSpan timeout)
    : EfLensException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"Query cancelled after {timeout.TotalSeconds:0.#} s. Add filters, aggregate, or select fewer columns."));
