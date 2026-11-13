using EfLens.Providers;

namespace EfLens.Tests;

/// <summary>The T-SQL gate, tested without a database: it must not be fooled by comments, literals or CTEs.</summary>
public sealed class SqlServerQueryValidatorTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT TOP 5 * FROM sales.Orders ORDER BY Total DESC")]
    [InlineData("SELECT * FROM dbo.Customers WHERE Name = 'DROP TABLE Customers; --'")]
    [InlineData("WITH t AS (SELECT 1 AS x) SELECT x FROM t")]
    [InlineData("SELECT 1 UNION ALL SELECT 2")]
    [InlineData("/* DELETE FROM t; */ SELECT 1 -- ; DROP TABLE t")]
    [InlineData("SELECT * FROM dbo.Customers WITH (NOLOCK)")]
    [InlineData("SELECT 1;")]
    public void Accepts_a_single_plain_select(string sql) => SqlServerQueryValidator.Validate(sql);

    [Theory]
    [InlineData("INSERT INTO t VALUES (1)", "INSERT statements")]
    [InlineData("UPDATE t SET a = 1", "UPDATE statements")]
    [InlineData("DELETE FROM t", "DELETE statements")]
    [InlineData("MERGE t USING s ON t.id = s.id WHEN MATCHED THEN DELETE;", "MERGE statements")]
    [InlineData("DROP TABLE t", "DROP TABLE statements")]
    [InlineData("TRUNCATE TABLE t", "TRUNCATE TABLE statements")]
    [InlineData("EXEC sp_who", "EXECUTE statements")]
    [InlineData("WAITFOR DELAY '00:00:10'", "WAIT FOR statements")]
    [InlineData("WITH x AS (SELECT 1 AS a) DELETE FROM t", "DELETE statements")]
    [InlineData("SELECT * INTO copy FROM t", "INTO creates a table")]
    [InlineData("SELECT 1; DROP TABLE t", "2 statements")]
    [InlineData("SELECT 1\nGO\nSELECT 2", "2 statements")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;Trusted_Connection=yes;', 'SELECT 1') AS r", "OPENROWSET")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'C:\\secret.txt', SINGLE_CLOB) AS f", "OPENROWSET")]
    [InlineData("SELECT * FROM OPENQUERY(remote, 'SELECT 1')", "OPENQUERY")]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t", "OPENDATASOURCE")]
    [InlineData("SELECT * FROM remote.shop.dbo.Customers", "linked server")]
    [InlineData("SELECT NEXT VALUE FOR dbo.OrderNumbers", "NEXT VALUE FOR")]
    [InlineData("SELECT * FROM t WITH (TABLOCKX)", "TABLOCKX")]
    [InlineData("SELECT * FROM t WITH (UPDLOCK, HOLDLOCK)", "UPDLOCK")]
    // T-SQL lets you call a procedure without EXEC, so a typo is still caught, as a procedure call.
    [InlineData("SELEC 1", "EXECUTE statements")]
    [InlineData("SELECT FROM WHERE", "syntax error")]
    [InlineData("   ", "empty")]
    [InlineData("-- just a comment", "no statement")]
    public void Rejects_anything_else_with_a_reason(string sql, string reason)
    {
        var ex = Assert.Throws<QueryRejectedException>(() => SqlServerQueryValidator.Validate(sql));

        Assert.Contains(reason, ex.Message);
        Assert.Contains("one read-only SELECT", ex.Message);
    }
}
