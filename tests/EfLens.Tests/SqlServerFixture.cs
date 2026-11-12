using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace EfLens.Tests;

/// <summary>
/// One SQL Server container for every SQL Server test. Without Docker the tests are skipped locally;
/// CI sets EFLENS_REQUIRE_DOCKER=1 so a missing Docker fails the build instead of hiding the tests.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-latest";

    private MsSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new MsSqlBuilder(Image).Build();
            await _container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("EFLENS_REQUIRE_DOCKER") != "1")
        {
            SkipReason = $"SQL Server container unavailable (is Docker running?): {ex.GetType().Name}";
            return;
        }

        var master = _container.GetConnectionString();
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync();
            await new SqlCommand("CREATE DATABASE EfLensTest", connection).ExecuteNonQueryAsync();
        }

        ConnectionString = new SqlConnectionStringBuilder(master) { InitialCatalog = "EfLensTest" }.ConnectionString;

        await using var db = new SqlConnection(ConnectionString);
        await db.OpenAsync();
        string[] batches =
        [
            "CREATE SCHEMA sales",
            """
            CREATE TABLE dbo.Customers (
                Id int IDENTITY(1,1) CONSTRAINT PK_Customers PRIMARY KEY,
                Name nvarchar(100) NOT NULL,
                Email varchar(200) NULL CONSTRAINT UQ_Customers_Email UNIQUE,
                Notes nvarchar(max) NULL)
            """,
            """
            CREATE TABLE sales.Orders (
                Id int NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
                CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES dbo.Customers(Id) ON DELETE CASCADE,
                Total decimal(10,2) NOT NULL CONSTRAINT DF_Orders_Total DEFAULT 0,
                PlacedAt datetime2(3) NOT NULL)
            """,
            "CREATE INDEX IX_Orders_CustomerId ON sales.Orders (CustomerId)",
            """
            CREATE TABLE sales.OrderLines (
                OrderId int NOT NULL,
                LineNo int NOT NULL,
                Product nvarchar(50) NOT NULL,
                CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNo),
                CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderId) REFERENCES sales.Orders(Id))
            """,
            "CREATE TABLE dbo.Audit (Id int NOT NULL)",
            "CREATE TABLE sales.Audit (Id int NOT NULL)",
            "CREATE VIEW dbo.CustomerTotals AS SELECT CustomerId, SUM(Total) AS Total FROM sales.Orders GROUP BY CustomerId",
        ];
        foreach (var batch in batches)
            await new SqlCommand(batch, db).ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
