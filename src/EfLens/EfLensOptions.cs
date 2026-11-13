namespace EfLens;

/// <summary>Connection settings and query limits.</summary>
public sealed class EfLensOptions
{
    public const string SectionName = "EfLens";

    /// <summary>Database provider. Supported today: <c>sqlite</c>, <c>sqlserver</c> (alias <c>mssql</c>).</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>ADO.NET connection string. EfLens always opens it read-only.</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>Most rows the <c>query</c> tool returns; the agent can ask for fewer, never more.</summary>
    public int MaxRows { get; set; } = 200;

    /// <summary>Seconds a query may run before it is cancelled in the engine.</summary>
    public int QueryTimeoutSeconds { get; set; } = 15;

    /// <summary>Path to the compiled app (its .dll in bin/...) that contains the DbContext. Optional.</summary>
    public string? EfAssembly { get; set; }

    /// <summary>DbContext to use when the assembly has more than one. Optional.</summary>
    public string? EfContext { get; set; }
}
