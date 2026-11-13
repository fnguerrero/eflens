// Shared by EfLens and EfLens.ModelProbe (linked source, not a shared assembly): the probe lives in another
// AssemblyLoadContext, so the two sides only exchange JSON with this shape.
namespace EfLens.Ef;

/// <summary>What EF Core believes the database looks like, read from a DbContext.</summary>
/// <param name="Context">Full name of the DbContext type.</param>
/// <param name="Provider">EF Core provider, e.g. <c>Microsoft.EntityFrameworkCore.SqlServer</c>.</param>
/// <param name="EfCoreVersion">EF Core version the target app runs.</param>
/// <param name="Entities">Entity types and the tables their properties map to.</param>
/// <param name="Tables">The store side: tables EF Core expects, with keys, foreign keys and indexes.</param>
public sealed record EfModelInfo(
    string Context,
    string Provider,
    string EfCoreVersion,
    IReadOnlyList<EfEntityInfo> Entities,
    IReadOnlyList<EfTableInfo> Tables);

/// <summary>An entity type and where it is stored.</summary>
public sealed record EfEntityInfo(string Entity, string ClrType, IReadOnlyList<EfTableMapping> Mappings);

/// <summary>Property to column mapping for one table or view (several when the entity is split).</summary>
/// <param name="Kind"><c>table</c> or <c>view</c>.</param>
public sealed record EfTableMapping(string? Schema, string Name, string Kind, IReadOnlyList<EfColumnMapping> Columns);

public sealed record EfColumnMapping(string Property, string Column);

/// <summary>A table as EF Core would create it.</summary>
/// <param name="ExcludedFromMigrations">True when migrations do not manage this table.</param>
public sealed record EfTableInfo(
    string? Schema,
    string Name,
    bool ExcludedFromMigrations,
    IReadOnlyList<EfColumnInfo> Columns,
    EfKeyInfo? PrimaryKey,
    IReadOnlyList<EfKeyInfo> UniqueConstraints,
    IReadOnlyList<EfForeignKeyInfo> ForeignKeys,
    IReadOnlyList<EfIndexInfo> Indexes);

/// <param name="StoreType">Database type, e.g. <c>nvarchar(100)</c>.</param>
/// <param name="DefaultValue">Default as SQL (<c>HasDefaultValueSql</c>) or as the literal value (<c>HasDefaultValue</c>).</param>
public sealed record EfColumnInfo(string Name, string StoreType, bool IsNullable, string? DefaultValue);

public sealed record EfKeyInfo(string? Name, IReadOnlyList<string> Columns);

public sealed record EfForeignKeyInfo(
    string? Name,
    IReadOnlyList<string> Columns,
    string? PrincipalSchema,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumns,
    string OnDelete);

public sealed record EfIndexInfo(string? Name, IReadOnlyList<string> Columns, bool IsUnique);
