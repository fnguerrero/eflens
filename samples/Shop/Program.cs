using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shop;

// dotnet Shop.dll migrate "<connection string>" [target migration]
// Used by EfLens' tests (and handy for demos) to build a database at a known migration.
if (args is ["migrate", var connection, ..])
{
    using var context = new ShopContext(new DbContextOptionsBuilder<ShopContext>().UseSqlServer(connection).Options);
    var target = args.Length > 2 ? args[2] : null;
    context.GetService<IMigrator>().Migrate(target);
    Console.WriteLine($"Shop database migrated to {target ?? "the latest migration"}.");
    return 0;
}

Console.WriteLine("Shop: sample app for EfLens. Point EfLens at this assembly with --ef-assembly.");
Console.WriteLine("To create a database: dotnet Shop.dll migrate \"<connection string>\" [migration]");
return 0;
