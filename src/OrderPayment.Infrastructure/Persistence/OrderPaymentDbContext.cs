using Microsoft.EntityFrameworkCore;
using OrderPayment.Domain;

namespace OrderPayment.Infrastructure.Persistence;

public sealed partial class OrderPaymentDbContext(DbContextOptions<OrderPaymentDbContext> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var product = modelBuilder.Entity<Product>();
        product.ToTable("Products", table =>
        {
            table.HasCheckConstraint("CK_Products_Price", "[Price] >= 0");
            table.HasCheckConstraint("CK_Products_StockQuantity", "[StockQuantity] >= 0");
        });
        product.HasKey(x => x.Id);
        product.Property(x => x.Sku).HasMaxLength(64).IsRequired();
        product.HasIndex(x => x.Sku).IsUnique();
        product.Property(x => x.Name).HasMaxLength(200).IsRequired();
        product.Property(x => x.Price).HasPrecision(18, 2);
        product.Property(x => x.RowVersion).IsRowVersion();

        var customer = modelBuilder.Entity<Customer>();
        customer.ToTable("Customers");
        customer.HasKey(x => x.Id);
        customer.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        customer.Property(x => x.Email).HasMaxLength(254).IsRequired();
        customer.HasIndex(x => x.Email).IsUnique();
        customer.Property(x => x.Phone).HasMaxLength(30);
        customer.Property(x => x.RowVersion).IsRowVersion();

        var order = modelBuilder.Entity<Order>();
        order.ToTable("Orders");
        order.HasKey(x => x.Id);
        order.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        order.Property(x => x.TotalAmount).HasPrecision(18, 2);
        order.Property(x => x.RowVersion).IsRowVersion();
        order.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        order.HasIndex(x => new { x.CustomerId, x.CreatedAt })
            .IncludeProperties(x => new { x.Status, x.TotalAmount });
        order.HasIndex(x => x.CreatedAt).HasDatabaseName("IX_Orders_CreatedAt_Revenue")
            .IncludeProperties(x => new { x.CustomerId, x.Status, x.TotalAmount });
        order.HasMany(x => x.Items).WithOne().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        order.Navigation(x => x.Items).HasField("_items").UsePropertyAccessMode(PropertyAccessMode.Field);

        var item = modelBuilder.Entity<OrderItem>();
        item.ToTable("OrderItems", table => table.HasCheckConstraint("CK_OrderItems_Quantity", "[Quantity] > 0"));
        item.HasKey(x => x.Id);
        item.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
        item.Property(x => x.UnitPrice).HasPrecision(18, 2);
        item.Ignore(x => x.LineTotal);
        item.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
        ConfigurePayments(modelBuilder);
        ConfigureJobs(modelBuilder);
    }
}
