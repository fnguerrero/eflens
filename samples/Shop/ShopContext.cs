using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Shop;

public sealed class Customer
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Email { get; set; }
    public string? Notes { get; set; }
    public List<Order> Orders { get; } = [];
}

public sealed class Order
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public decimal Total { get; set; }
    public DateTime PlacedAt { get; set; }
    public string Status { get; set; } = "placed";
    public List<OrderLine> Lines { get; } = [];
}

public sealed class OrderLine
{
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public required string Product { get; set; }
}

/// <summary>A small shop: the same schema EfLens' SQL Server tests create, mapped with EF Core.</summary>
public sealed class ShopContext(DbContextOptions<ShopContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(customer =>
        {
            customer.Property(c => c.Name).HasMaxLength(100);
            customer.Property(c => c.Email).HasMaxLength(200).IsUnicode(false);
            customer.HasIndex(c => c.Email).IsUnique();
        });

        modelBuilder.Entity<Order>(order =>
        {
            order.ToTable("Orders", "sales");
            order.Property(o => o.Id).ValueGeneratedNever();
            order.Property(o => o.Total).HasPrecision(10, 2).HasDefaultValue(0m);
            order.Property(o => o.PlacedAt).HasPrecision(3);
            order.Property(o => o.Status).HasMaxLength(20).HasDefaultValue("placed");
            order.HasOne(o => o.Customer).WithMany(c => c.Orders).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OrderLine>(line =>
        {
            line.ToTable("OrderLines", "sales");
            line.HasKey(l => new { l.OrderId, l.LineNumber });
            line.Property(l => l.Product).HasMaxLength(50);
            line.HasOne<Order>().WithMany(o => o.Lines).HasForeignKey(l => l.OrderId);
        });
    }
}

/// <summary>What `dotnet ef` (and EfLens) use to build the context at design time. Nothing connects here.</summary>
public sealed class ShopContextFactory : IDesignTimeDbContextFactory<ShopContext>
{
    public ShopContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ShopContext>()
            .UseSqlServer("Server=localhost;Database=Shop;Trusted_Connection=True;TrustServerCertificate=True")
            .Options);
}
