namespace EfLens;

/// <summary>Connection settings for the database EfLens inspects.</summary>
public sealed class EfLensOptions
{
    public const string SectionName = "EfLens";

    /// <summary>Database provider. Supported today: <c>sqlite</c>.</summary>
    public string Provider { get; set; } = "sqlite";

    /// <summary>ADO.NET connection string. EfLens always opens it read-only.</summary>
    public string ConnectionString { get; set; } = "";
}
