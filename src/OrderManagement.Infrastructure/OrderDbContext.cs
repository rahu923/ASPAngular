using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using OrderManagement.Domain;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
namespace OrderManagement.Infrastructure;
public class ApplicationUser : IdentityUser { }
// IdentityDbContext includes Identity tables; business tables share the same SQL database.
// IDataProtectionKeyContext lets Production persist its encrypted cryptographic key ring here.
public class OrderDbContext : IdentityDbContext<ApplicationUser>, IDataProtectionKeyContext
{
    public OrderDbContext(DbContextOptions<OrderDbContext> options) : base(options) { }
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<RefreshFamily> RefreshFamilies => Set<RefreshFamily>();
    public DbSet<RefreshSession> RefreshSessions => Set<RefreshSession>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    protected override void OnModelCreating(ModelBuilder model)
    {
        // Preserve Identity's keys, indexes and relationships before adding business mappings.
        base.OnModelCreating(model);
        model.Entity<RefreshFamily>(f =>
        {
            f.Property(x => x.SecurityStamp).HasMaxLength(100).IsRequired();
            f.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            f.HasIndex(x => x.UserId);
        });
        model.Entity<RefreshSession>(s =>
        {
            // SHA-256 is 32 bytes, represented here by 64 hexadecimal characters.
            // The unique index supports lookup and prevents storing duplicate token hashes.
            s.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            s.HasIndex(x => x.TokenHash).IsUnique();
            s.HasOne(x => x.Family).WithMany().HasForeignKey(x => x.FamilyId);
        });
        model.Entity<Product>(p =>
        {
            p.Property(x => x.Name).HasMaxLength(160).IsRequired();
            p.Property(x => x.Sku).HasMaxLength(40).IsRequired();
            p.Property(x => x.Description).HasMaxLength(2000);
            p.HasIndex(x => x.Sku).IsUnique();
            // 18 total decimal digits, two fractional digits. Money uses decimal, not float/double.
            p.Property(x => x.Price).HasPrecision(18, 2);
            // SQL Server generates rowversion on writes; EF includes the original value in
            // UPDATE/DELETE predicates and raises a concurrency exception if no row matches.
            p.Property(x => x.RowVersion).IsRowVersion();
            // Database checks remain authoritative even if a caller bypasses API validation.
            p.HasCheckConstraint("CK_Product_Price", "[Price] >= 0");
            p.HasCheckConstraint("CK_Product_Stock", "[StockQuantity] >= 0");
            // Avoid global active filters: historical orders must still resolve products.
            p.HasData(new Product { Id = 1, Name = "Mechanical Keyboard", Sku = "KEY-001", Description = "Compact keyboard for your development desk", Price = 3499m, StockQuantity = 25 },
                new Product { Id = 2, Name = "USB-C Hub", Sku = "HUB-001", Description = "Connect your daily essentials", Price = 1799m, StockQuantity = 40 });
        });
        model.Entity<Order>(o =>
        {
            o.Property(x => x.TotalAmount).HasPrecision(18, 2);
            o.Property(x => x.RowVersion).IsRowVersion();
            // Composite index starts with the ownership filter, then the history timestamp.
            o.HasIndex(x => new { x.CustomerId, x.CreatedAtUtc });
            o.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            o.HasCheckConstraint("CK_Order_Total", "[TotalAmount] >= 0");
        });
        model.Entity<OrderItem>(i =>
        {
            i.Property(x => x.UnitPrice).HasPrecision(18, 2);
            // A product appears once per order; change its quantity instead of duplicating lines.
            i.HasIndex(x => new { x.OrderId, x.ProductId }).IsUnique();
            i.HasOne(x => x.Order).WithMany(x => x.Items).HasForeignKey(x => x.OrderId);
            // Restrict physical deletion of referenced products; deactivation preserves history.
            i.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            i.HasCheckConstraint("CK_OrderItem_Quantity", "[Quantity] > 0");
            i.HasCheckConstraint("CK_OrderItem_Price", "[UnitPrice] >= 0");
        });
    }
}
