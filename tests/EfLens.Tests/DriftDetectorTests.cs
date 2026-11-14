using EfLens.Ef;
using EfLens.Providers;

namespace EfLens.Tests;

/// <summary>Every drift rule, without a database: a small model against hand-built catalog data.</summary>
public sealed class DriftDetectorTests
{
    private static readonly string[] Both = ["001_InitialCreate", "002_AddOrderStatus"];

    private static EfModelInfo Model(bool? pendingModelChanges = false) => new(
        "Shop.ShopContext", "Microsoft.EntityFrameworkCore.SqlServer", "10.0.15", [],
        [
            new(null, "Customers", false,
                [new("Id", "int", false, null), new("Name", "nvarchar(100)", false, null), new("Email", "varchar(200)", true, null)],
                new EfKeyInfo("PK_Customers", ["Id"]), [], [],
                [new EfIndexInfo("IX_Customers_Email", ["Email"], true)]),
            new("sales", "Orders", false,
                [new("Id", "int", false, null), new("CustomerId", "int", false, null),
                 new("Total", "decimal(10,2)", false, "0"), new("PlacedAt", "datetime2", false, null)],
                new EfKeyInfo("PK_Orders", ["Id"]), [],
                [new EfForeignKeyInfo("FK_Orders_Customers_CustomerId", ["CustomerId"], null, "Customers", ["Id"], "CASCADE")],
                [new EfIndexInfo("IX_Orders_CustomerId", ["CustomerId"], false)]),
        ],
        new EfMigrationsInfo(Both, pendingModelChanges, null, "__EFMigrationsHistory"));

    /// <summary>What SQL Server reports for a database created by those migrations.</summary>
    private static List<DatabaseTable> Database(
        Func<List<ColumnInfo>, List<ColumnInfo>>? customers = null,
        Func<List<ColumnInfo>, List<ColumnInfo>>? orders = null,
        bool customerIndex = true,
        string onDelete = "CASCADE") =>
    [
        new(new TableDetails("dbo", "Customers", "table",
            (customers ?? (c => c))([new("Id", "int", false, null, 1), new("Name", "nvarchar(100)", false, null, null), new("Email", "varchar(200)", true, null, null)]),
            [new("PK_Customers", ["Id"], true, true), .. customerIndex ? [new IndexInfo("IX_Customers_Email", ["Email"], true, false)] : Array.Empty<IndexInfo>()]),
            []),
        new(new TableDetails("sales", "Orders", "table",
            (orders ?? (c => c))([new("Id", "int", false, null, 1), new("CustomerId", "int", false, null, null),
                new("Total", "decimal(10,2)", false, "((0.0))", null), new("PlacedAt", "datetime2(7)", false, null, null)]),
            [new("PK_Orders", ["Id"], true, true), new("IX_Orders_CustomerId", ["CustomerId"], false, false)]),
            [new("FK_Orders_Customers_CustomerId", "sales", "Orders", ["CustomerId"], "dbo", "Customers", ["Id"], onDelete)]),
        new(new TableDetails("dbo", "__EFMigrationsHistory", "table",
            [new("MigrationId", "nvarchar(150)", false, null, 1), new("ProductVersion", "nvarchar(32)", false, null, null)],
            [new("PK___EFMigrationsHistory", ["MigrationId"], true, true)]),
            []),
    ];

    private static IEnumerable<(string, string, string?)> Kinds(EfDriftReport report) =>
        report.Differences.Select(d => (d.Kind, d.Table, d.Object));

    [Fact]
    public void A_database_built_by_every_migration_is_in_sync()
    {
        var report = DriftDetector.Compare(Model(), Both, Database());

        Assert.True(report.InSync);
        Assert.Empty(report.Differences);
        Assert.Empty(report.Migrations.Pending);
        Assert.Equal("In sync: 2 migrations applied and the schema matches the model.", report.Summary);
    }

    [Fact]
    public void Pending_and_unknown_migrations_are_listed()
    {
        var report = DriftDetector.Compare(Model(), ["001_InitialCreate", "001b_Hotfix"], Database());

        Assert.False(report.InSync);
        Assert.Equal(["002_AddOrderStatus"], report.Migrations.Pending);
        Assert.Equal(["001b_Hotfix"], report.Migrations.NotInAssembly);
        Assert.Contains("1 pending migration (002_AddOrderStatus)", report.Summary);
        Assert.Contains("missing from this build (001b_Hotfix)", report.Summary);
    }

    [Fact]
    public void A_database_without_history_has_every_migration_pending()
    {
        var report = DriftDetector.Compare(Model(), applied: null, Database());

        Assert.False(report.Migrations.HistoryTableExists);
        Assert.Equal(Both, report.Migrations.Pending);
        Assert.Contains("there is no __EFMigrationsHistory table", report.Summary);
    }

    [Fact]
    public void Model_changes_without_a_migration_are_not_in_sync()
    {
        var report = DriftDetector.Compare(Model(pendingModelChanges: true), Both, Database());

        Assert.False(report.InSync);
        Assert.Contains("dotnet ef migrations add", report.Summary);
    }

    [Fact]
    public void Hand_made_changes_show_up_one_by_one()
    {
        var database = Database(
            customers: c => [c[0], c[1] with { DataType = "nvarchar(200)" }, c[2], new("Phone", "varchar(30)", true, null, null)],
            orders: c => [c[0], c[1], c[2] with { IsNullable = true }],
            customerIndex: false,
            onDelete: "NO ACTION");
        database.Add(new(new TableDetails("dbo", "Audit", "table", [new("Id", "int", false, null, 1)], []), []));

        var report = DriftDetector.Compare(Model(), Both, database);

        Assert.False(report.InSync);
        Assert.Equal(
            [
                ("type_mismatch", "Customers", "Name"),
                ("extra_column", "Customers", "Phone"),
                ("missing_index", "Customers", "IX_Customers_Email"),
                ("nullability_mismatch", "sales.Orders", "Total"),
                ("missing_column", "sales.Orders", "PlacedAt"),
                ("on_delete_mismatch", "sales.Orders", "FK_Orders_Customers_CustomerId"),
                ("extra_table", "dbo.Audit", null),
            ],
            Kinds(report));

        var type = report.Differences[0];
        Assert.Equal(("nvarchar(100)", "nvarchar(200)"), (type.Expected, type.Actual));
        Assert.Equal("datetime2 NOT NULL", report.Differences.Single(d => d.Kind == "missing_column").Expected);
        Assert.Contains("1 schema difference", DriftDetector.Compare(Model(), Both, Database(customerIndex: false)).Summary);
    }

    [Fact]
    public void Missing_tables_and_changed_keys_are_reported()
    {
        var database = Database(orders: c => [c[0] with { PrimaryKeyOrdinal = null }, c[1] with { PrimaryKeyOrdinal = 1 }, c[2], c[3]]);
        database.RemoveAt(0); // no Customers table

        var report = DriftDetector.Compare(Model(), Both, database);

        Assert.Equal(
            [
                ("missing_table", "Customers", null),
                ("primary_key_mismatch", "sales.Orders", "PK_Orders"),
            ],
            Kinds(report));
        Assert.Equal(("(Id)", "(CustomerId)"), (report.Differences[1].Expected, report.Differences[1].Actual));
    }

    [Fact]
    public void Foreign_keys_and_indexes_are_matched_by_columns_not_by_name()
    {
        var database = Database();
        database[1] = database[1] with
        {
            Details = database[1].Details with { Indexes = [new("PK_Orders", ["Id"], true, true), new("ix_custom_name", ["customerid"], false, false)] },
            ForeignKeys = [database[1].ForeignKeys[0] with { Name = "FK_legacy" }],
        };

        Assert.True(DriftDetector.Compare(Model(), Both, database).InSync);
    }

    [Fact]
    public void Restrict_and_no_action_mean_the_same()
    {
        var model = Model();
        model = model with
        {
            Tables = [model.Tables[0], model.Tables[1] with { ForeignKeys = [model.Tables[1].ForeignKeys[0] with { OnDelete = "RESTRICT" }] }],
        };

        Assert.True(DriftDetector.Compare(model, Both, Database(onDelete: "NO ACTION")).InSync);
    }

    [Theory]
    [InlineData("datetime2", "datetime2(7)")]
    [InlineData("DateTimeOffset", "datetimeoffset(7)")]
    [InlineData("rowversion", "timestamp")]
    [InlineData("float(53)", "float")]
    [InlineData("numeric(10, 2)", "decimal(10,2)")]
    [InlineData("decimal", "decimal(18,0)")]
    [InlineData("nvarchar(max)", "nvarchar(max)")]
    [InlineData("INTEGER", "integer")]
    public void Store_types_are_compared_in_one_spelling(string type, string normalized) =>
        Assert.Equal(normalized, DriftDetector.NormalizeType(type));
}
