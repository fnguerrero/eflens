using EfLens.Providers;
using EfLens.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace EfLens;

public static class EfLensServiceCollectionExtensions
{
    /// <summary>Registers the schema provider selected by <paramref name="options"/>.</summary>
    public static IServiceCollection AddEfLens(this IServiceCollection services, EfLensOptions options)
    {
        IDatabaseProvider provider = options.Provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" => new SqliteSchemaProvider(options.ConnectionString),
            "sqlserver" or "mssql" => new SqlServerSchemaProvider(options.ConnectionString),
            var other => throw new NotSupportedException(
                $"Provider '{other}' is not supported yet. Supported providers: sqlite, sqlserver."),
        };

        services.AddSingleton(options);
        services.AddSingleton<ISchemaProvider>(provider);
        services.AddSingleton<IQueryRunner>(provider);
        return services;
    }

    /// <summary>Adds every EfLens tool to the MCP server.</summary>
    public static IMcpServerBuilder WithEfLensTools(this IMcpServerBuilder builder) =>
        builder.WithTools<SchemaTools>().WithTools<QueryTools>();
}
