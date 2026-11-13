using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

namespace EfLens.Ef;

/// <summary>
/// Entry point called by EfLens through reflection, inside the target app's load context.
/// Builds the DbContext the same way <c>dotnet ef</c> does at design time and never opens a connection.
/// </summary>
public static class ModelProbe
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <returns>JSON: <c>{"model": EfModelInfo}</c> or <c>{"error": "message for the agent"}</c>.</returns>
    public static string Describe(Assembly assembly, string? contextName)
    {
        try
        {
            using var context = CreateContext(assembly, contextName);
            return JsonSerializer.Serialize(new { model = Read(context) }, Json);
        }
        catch (ProbeException ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message }, Json);
        }
    }

    private static DbContext CreateContext(Assembly assembly, string? contextName)
    {
        var types = LoadableTypes(assembly);
        var contexts = types
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false } && typeof(DbContext).IsAssignableFrom(t))
            .ToList();

        if (contextName is not null)
            contexts = contexts.Where(t => t.Name == contextName || t.FullName == contextName).ToList();

        var contextType = contexts.Count switch
        {
            1 => contexts[0],
            0 when contextName is not null => throw new ProbeException(
                $"No DbContext named '{contextName}' in {assembly.GetName().Name}."),
            0 => throw new ProbeException($"No DbContext found in {assembly.GetName().Name}."),
            _ => throw new ProbeException(
                $"Found several DbContexts ({string.Join(", ", contexts.Select(c => c.Name))}). Choose one with the context argument."),
        };

        // 1. A design-time factory, exactly what `dotnet ef` looks for first.
        var factoryInterface = typeof(IDesignTimeDbContextFactory<>).MakeGenericType(contextType);
        var factory = types.FirstOrDefault(t => t is { IsClass: true, IsAbstract: false } && factoryInterface.IsAssignableFrom(t));
        if (factory is not null)
        {
            var instance = Activator.CreateInstance(factory)
                ?? throw new ProbeException($"Could not create {factory.Name}.");
            return (DbContext)factoryInterface.GetMethod(nameof(IDesignTimeDbContextFactory<DbContext>.CreateDbContext))!
                .Invoke(instance, [Array.Empty<string>()])!;
        }

        // 2. A parameterless constructor that configures the provider in OnConfiguring.
        if (contextType.GetConstructor(Type.EmptyTypes) is not null)
            return (DbContext)Activator.CreateInstance(contextType)!;

        throw new ProbeException(
            $"Cannot create {contextType.Name}: add an IDesignTimeDbContextFactory<{contextType.Name}> to the assembly " +
            "(the same thing `dotnet ef` needs) or a parameterless constructor.");
    }

    private static EfModelInfo Read(DbContext context)
    {
        // The design-time model keeps everything; the runtime model may be trimmed (compiled models).
        var model = context.GetService<IDesignTimeModel>().Model;
        var relational = model.GetRelationalModel();

        var entities = model.GetEntityTypes()
            .OrderBy(e => e.DisplayName(), StringComparer.Ordinal)
            .Select(e => new EfEntityInfo(
                e.DisplayName(),
                e.ClrType.FullName ?? e.ClrType.Name,
                [
                    .. e.GetTableMappings().Select(m => new EfTableMapping(m.Table.Schema, m.Table.Name, "table",
                        [.. m.ColumnMappings.Select(c => new EfColumnMapping(c.Property.Name, c.Column.Name))])),
                    .. e.GetViewMappings().Select(m => new EfTableMapping(m.View.Schema, m.View.Name, "view",
                        [.. m.ColumnMappings.Select(c => new EfColumnMapping(c.Property.Name, c.Column.Name))])),
                ]))
            .ToList();

        var tables = relational.Tables
            .OrderBy(t => t.Schema, StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new EfTableInfo(
                t.Schema,
                t.Name,
                t.IsExcludedFromMigrations,
                [.. t.Columns.Select(c => new EfColumnInfo(c.Name, c.StoreType, c.IsNullable,
                    c.DefaultValueSql ?? (c.DefaultValue is { } v ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null)))],
                t.PrimaryKey is { } pk ? new EfKeyInfo(pk.Name, [.. pk.Columns.Select(c => c.Name)]) : null,
                [.. t.UniqueConstraints.Where(u => u != t.PrimaryKey).OrderBy(u => u.Name, StringComparer.Ordinal)
                    .Select(u => new EfKeyInfo(u.Name, [.. u.Columns.Select(c => c.Name)]))],
                [.. t.ForeignKeyConstraints.OrderBy(f => f.Name, StringComparer.Ordinal)
                    .Select(f => new EfForeignKeyInfo(
                        f.Name,
                        [.. f.Columns.Select(c => c.Name)],
                        f.PrincipalTable.Schema,
                        f.PrincipalTable.Name,
                        [.. f.PrincipalColumns.Select(c => c.Name)],
                        Describe(f.OnDeleteAction)))],
                [.. t.Indexes.OrderBy(i => i.Name, StringComparer.Ordinal)
                    .Select(i => new EfIndexInfo(i.Name, [.. i.Columns.Select(c => c.Name)], i.IsUnique))]))
            .ToList();

        return new EfModelInfo(
            context.GetType().FullName ?? context.GetType().Name,
            context.Database.ProviderName ?? "unknown",
            typeof(DbContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? typeof(DbContext).Assembly.GetName().Version?.ToString() ?? "unknown",
            entities,
            tables);
    }

    /// <summary>Same wording the schema tools use for the database side, so both can be compared.</summary>
    private static string Describe(ReferentialAction action) => action switch
    {
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        ReferentialAction.Restrict => "RESTRICT",
        _ => "NO ACTION",
    };

    private static List<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return [.. assembly.GetTypes()];
        }
        catch (ReflectionTypeLoadException ex)
        {
            return [.. ex.Types.OfType<Type>()];
        }
    }

    private sealed class ProbeException(string message) : Exception(message);
}
