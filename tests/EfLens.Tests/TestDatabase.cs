using Microsoft.Data.Sqlite;

namespace EfLens.Tests;

/// <summary>A throwaway SQLite file with a small known schema.</summary>
public sealed class TestDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eflens-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={Path};Pooling=False";

    public TestDatabase()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Customers (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL,
                Email TEXT UNIQUE);
            CREATE TABLE Orders (
                Id INTEGER PRIMARY KEY,
                CustomerId INTEGER NOT NULL REFERENCES Customers(Id) ON DELETE CASCADE,
                Total REAL DEFAULT 0);
            CREATE INDEX IX_Orders_CustomerId ON Orders (CustomerId);
            CREATE TABLE OrderLines (
                OrderId INTEGER NOT NULL,
                LineNo INTEGER NOT NULL,
                Product TEXT NOT NULL,
                PRIMARY KEY (OrderId, LineNo),
                FOREIGN KEY (OrderId) REFERENCES Orders);
            CREATE VIEW CustomerTotals AS SELECT CustomerId, SUM(Total) AS Total FROM Orders GROUP BY CustomerId;
            INSERT INTO Customers (Name) VALUES ('Ada');
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(Path);
    }
}
