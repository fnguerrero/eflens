using EfLens.Providers;
using Microsoft.Data.Sqlite;

namespace EfLens.Tests;

public sealed class SqliteMigrationHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task No_history_table_means_null()
    {
        using var db = new TestDatabase();

        Assert.Null(await new SqliteSchemaProvider(db.ConnectionString).GetAppliedMigrationsAsync(null, "__EFMigrationsHistory", Ct));
    }

    [Fact]
    public async Task Applied_migrations_come_back_in_order()
    {
        using var db = new TestDatabase();
        await using (var connection = new SqliteConnection(db.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE "__EFMigrationsHistory" (MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);
                INSERT INTO "__EFMigrationsHistory" VALUES ('002_AddOrderStatus', '10.0.15'), ('001_InitialCreate', '10.0.15');
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }

        var applied = await new SqliteSchemaProvider(db.ConnectionString).GetAppliedMigrationsAsync(null, "__efmigrationshistory", Ct);

        Assert.Equal(["001_InitialCreate", "002_AddOrderStatus"], applied);
    }
}
