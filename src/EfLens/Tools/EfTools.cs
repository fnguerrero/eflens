using System.ComponentModel;
using EfLens.Ef;
using ModelContextProtocol.Server;

namespace EfLens.Tools;

/// <summary>Tools that read the app's EF Core model. They load the compiled app; they never touch the database.</summary>
[McpServerToolType]
public sealed class EfTools(IEfModelSource source)
{
    [McpServerTool(Name = "ef_model", Title = "EF Core model",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("""
        Reads the EF Core model of the app configured with --ef-assembly, the way `dotnet ef` builds it at design time
        (IDesignTimeDbContextFactory first, then a parameterless constructor). Returns each entity with the table and
        columns its properties map to, and the tables EF Core expects: store types, nullability, defaults, keys,
        foreign keys and indexes. Compare it with describe_table to spot differences with the real database.
        """)]
    public Task<EfModelInfo> EfModel(
        [Description("DbContext class name, only needed when the app has more than one.")]
        string? context = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Expose(() => source.GetModelAsync(string.IsNullOrWhiteSpace(context) ? null : context, cancellationToken));
}
