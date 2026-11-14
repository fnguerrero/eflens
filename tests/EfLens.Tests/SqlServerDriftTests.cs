using System.Diagnostics;
using EfLens.Ef;
using EfLens.Providers;
using Microsoft.Data.SqlClient;

namespace EfLens.Tests;

/// <summary>
/// ef_drift end to end: samples/Shop applies its own migrations to a real SQL Server, then the database is changed
/// the way it happens in real life (a migration not deployed, a hotfix by hand) and EfLens has to find each change.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlServerDriftTests(SqlServerFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_database_migrated_to_the_latest_migration_is_in_sync()
    {
        var connection = await CreateShopDatabaseAsync("ShopLatest", targetMigration: null);

        var report = await DriftAsync(connection);

        Assert.True(report.InSync, string.Join(Environment.NewLine, report.Differences));
        Assert.Equal(2, report.Migrations.Applied.Count);
        Assert.Empty(report.Differences);
    }

    [Fact]
    public async Task A_missing_deploy_and_a_hand_made_hotfix_are_both_found()
    {
        var connection = await CreateShopDatabaseAsync("ShopDrifted", targetMigration: "InitialCreate");
        await ExecuteAsync(connection,
            "ALTER TABLE dbo.Customers ALTER COLUMN Name nvarchar(200) NOT NULL",
            "DROP INDEX IX_Customers_Email ON dbo.Customers",
            "ALTER TABLE sales.OrderLines ADD Discount decimal(5,2) NULL");

        var report = await DriftAsync(connection);

        Assert.False(report.InSync);
        Assert.EndsWith("_InitialCreate", Assert.Single(report.Migrations.Applied));
        Assert.EndsWith("_AddOrderStatus", Assert.Single(report.Migrations.Pending));
        Assert.Equal(
            [
                ("type_mismatch", "Customers", "Name", "nvarchar(100)", "nvarchar(200)"),
                ("missing_index", "Customers", "IX_Customers_Email", "UNIQUE (Email)", null),
                ("extra_column", "sales.OrderLines", "Discount", null, "decimal(5,2) NULL"),
                ("missing_column", "sales.Orders", "Status", "nvarchar(20) NOT NULL", null),
            ],
            report.Differences.Select(d => (d.Kind, d.Table, d.Object, d.Expected, d.Actual)));
        Assert.Contains("Pending migrations usually explain part of the differences", report.Summary);
    }

    [Fact]
    public async Task A_database_never_migrated_has_no_history()
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        var applied = await new SqlServerSchemaProvider(fixture.ConnectionString)
            .GetAppliedMigrationsAsync(null, "__EFMigrationsHistory", Ct);

        Assert.Null(applied);
    }

    private static Task<EfDriftReport> DriftAsync(string connection) =>
        new EfDriftService(
                new EfModelLoader(new EfLensOptions { EfAssembly = ShopSample.AssemblyPath }),
                new SqlServerSchemaProvider(connection))
            .CheckAsync(null, Ct);

    /// <summary>Creates an empty database and lets the sample app migrate it, like a deploy would.</summary>
    private async Task<string> CreateShopDatabaseAsync(string name, string? targetMigration)
    {
        Assert.SkipWhen(fixture.SkipReason is not null, fixture.SkipReason ?? "");

        var master = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = "master" }.ConnectionString;
        await ExecuteAsync(master, $"DROP DATABASE IF EXISTS [{name}]", $"CREATE DATABASE [{name}]");
        var connection = new SqlConnectionStringBuilder(fixture.ConnectionString) { InitialCatalog = name }.ConnectionString;

        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { ShopSample.AssemblyPath, "migrate", connection, targetMigration })
            if (argument is not null)
                start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(process.ExitCode == 0, $"Shop migrate failed: {await output}{await error}");
        return connection;
    }

    private static async Task ExecuteAsync(string connectionString, params string[] statements)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var sql in statements)
            await new SqlCommand(sql, connection).ExecuteNonQueryAsync(Ct);
    }
}
