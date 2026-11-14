using EfLens.Ef;

namespace EfLens.Tests;

/// <summary>Loads samples/Shop the way EfLens loads any app: from its build output, in an isolated load context.</summary>
public sealed class EfModelLoaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static EfModelLoader Loader(string? assembly = null, string? context = null) =>
        new(new EfLensOptions { EfAssembly = assembly ?? ShopSample.AssemblyPath, EfContext = context });

    [Fact]
    public async Task Builds_the_model_with_the_apps_own_ef_core_and_design_time_factory()
    {
        var model = await Loader().GetModelAsync(null, Ct);

        Assert.Equal("Shop.ShopContext", model.Context);
        Assert.Equal("Microsoft.EntityFrameworkCore.SqlServer", model.Provider);
        Assert.StartsWith("10.", model.EfCoreVersion);
    }

    [Fact]
    public async Task Tables_carry_the_store_types_ef_core_would_create()
    {
        var model = await Loader().GetModelAsync(null, Ct);

        var customers = model.Tables.Single(t => t.Name == "Customers");
        Assert.Null(customers.Schema); // default schema
        Assert.Equal(
            [("Id", "int", false), ("Email", "varchar(200)", true), ("Name", "nvarchar(100)", false), ("Notes", "nvarchar(max)", true)],
            customers.Columns.Select(c => (c.Name, c.StoreType, c.IsNullable)));
        Assert.Equal(("IX_Customers_Email", true), customers.Indexes.Select(i => (i.Name!, i.IsUnique)).Single());

        var orders = model.Tables.Single(t => t is { Schema: "sales", Name: "Orders" });
        Assert.Equal("decimal(10,2)", orders.Columns.Single(c => c.Name == "Total").StoreType);
        Assert.Equal("0", orders.Columns.Single(c => c.Name == "Total").DefaultValue);
        Assert.Equal("datetime2(3)", orders.Columns.Single(c => c.Name == "PlacedAt").StoreType);
    }

    [Fact]
    public async Task Keys_and_foreign_keys_use_the_names_migrations_would_generate()
    {
        var model = await Loader().GetModelAsync(null, Ct);

        var lines = model.Tables.Single(t => t.Name == "OrderLines");
        Assert.Equal("PK_OrderLines", lines.PrimaryKey!.Name);
        Assert.Equal(["OrderId", "LineNumber"], lines.PrimaryKey.Columns);

        var fk = model.Tables.Single(t => t.Name == "Orders").ForeignKeys.Single();
        Assert.Equal("FK_Orders_Customers_CustomerId", fk.Name);
        Assert.Equal((["CustomerId"], "Customers", ["Id"], "CASCADE"), (fk.Columns.ToArray(), fk.PrincipalTable, fk.PrincipalColumns.ToArray(), fk.OnDelete),
            new TupleOfArraysComparer());
    }

    [Fact]
    public async Task Entities_map_properties_to_columns()
    {
        var model = await Loader().GetModelAsync(null, Ct);

        Assert.Equal(["Customer", "Order", "OrderLine"], model.Entities.Select(e => e.Entity));
        var order = model.Entities.Single(e => e.Entity == "Order");
        Assert.Equal("Shop.Order", order.ClrType);
        var mapping = Assert.Single(order.Mappings);
        Assert.Equal(("sales", "Orders", "table"), (mapping.Schema, mapping.Name, mapping.Kind));
        Assert.Contains(mapping.Columns, c => c is { Property: "PlacedAt", Column: "PlacedAt" });
    }

    [Fact]
    public async Task Migrations_come_from_the_assembly_with_the_history_table_ef_core_uses()
    {
        var migrations = (await Loader().GetModelAsync(null, Ct)).Migrations;

        Assert.Collection(migrations.Defined,
            id => Assert.EndsWith("_InitialCreate", id),
            id => Assert.EndsWith("_AddOrderStatus", id));
        Assert.False(migrations.HasPendingModelChanges);
        Assert.Equal((null, "__EFMigrationsHistory"), (migrations.HistorySchema, migrations.HistoryTable));
    }

    [Fact]
    public async Task Each_call_reloads_and_the_build_output_is_never_locked()
    {
        await Loader().GetModelAsync(null, Ct);
        await Loader().GetModelAsync(null, Ct);

        // A rebuild must be able to overwrite the DLL while EfLens is running.
        using var exclusive = File.Open(ShopSample.AssemblyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanWrite);
    }

    [Fact]
    public async Task Context_can_be_chosen_by_name_and_a_wrong_name_is_explained()
    {
        Assert.Equal("Shop.ShopContext", (await Loader(context: "ShopContext").GetModelAsync(null, Ct)).Context);

        var ex = await Assert.ThrowsAsync<EfModelException>(() => Loader().GetModelAsync("BillingContext", Ct));
        Assert.Contains("No DbContext named 'BillingContext'", ex.Message);
    }

    [Fact]
    public async Task Missing_configuration_or_file_says_what_to_do()
    {
        var notConfigured = await Assert.ThrowsAsync<EfModelException>(
            () => new EfModelLoader(new EfLensOptions()).GetModelAsync(null, Ct));
        Assert.Contains("--ef-assembly", notConfigured.Message);

        var missing = await Assert.ThrowsAsync<EfModelException>(
            () => Loader(Path.Combine(Path.GetTempPath(), "nope", "App.dll")).GetModelAsync(null, Ct));
        Assert.Contains("does not exist", missing.Message);
    }

    [Fact]
    public async Task An_assembly_without_ef_core_is_explained()
    {
        // EfLens itself has no EF Core dependency: a good stand-in for "the wrong DLL".
        var ex = await Assert.ThrowsAsync<EfModelException>(
            () => Loader(Path.Combine(AppContext.BaseDirectory, "EfLens.dll")).GetModelAsync(null, Ct));

        Assert.Contains("EF Core", ex.Message);
    }

    private sealed class TupleOfArraysComparer : IEqualityComparer<(string[], string, string[], string)>
    {
        public bool Equals((string[], string, string[], string) x, (string[], string, string[], string) y) =>
            x.Item1.SequenceEqual(y.Item1) && x.Item2 == y.Item2 && x.Item3.SequenceEqual(y.Item3) && x.Item4 == y.Item4;

        public int GetHashCode((string[], string, string[], string) obj) => obj.Item2.GetHashCode();
    }
}

public sealed class SharedFrameworkTests
{
    [Fact]
    public void AspNetCore_assemblies_are_found_next_to_the_running_runtime()
    {
        // Needed for apps whose DbContext uses ASP.NET Core types, e.g. IdentityDbContext.
        var path = SharedFramework.FindAspNetCore(new System.Reflection.AssemblyName("Microsoft.AspNetCore.Http.Abstractions"));

        Assert.NotNull(path);
        Assert.Contains("Microsoft.AspNetCore.App", path);
        Assert.Contains($"{Path.DirectorySeparatorChar}{Environment.Version.Major}.", path);
    }

    [Fact]
    public void Unknown_assemblies_are_left_to_the_default_context() =>
        Assert.Null(SharedFramework.FindAspNetCore(new System.Reflection.AssemblyName("Not.A.Real.Assembly")));
}
