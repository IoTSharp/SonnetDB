using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace InventoryMigrationApp;

/// <summary>库存应用的实际业务上下文。</summary>
/// <param name="options">应用数据库配置。</param>
public sealed class InventoryContext(DbContextOptions<InventoryContext> options) : DbContext(options)
{
    /// <summary>读取和维护产品库存。</summary>
    public DbSet<Product> Products => Set<Product>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) => Configure(modelBuilder, upgraded: true);

    internal static void Configure(ModelBuilder modelBuilder, bool upgraded)
    {
        modelBuilder.Entity<Category>(entity =>
        {
            entity.ToTable("Categories");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Name).IsRequired();
            entity.HasOne<Category>().WithMany().HasForeignKey(row => row.ParentId)
                .HasConstraintName("FK_Categories_Parent").OnDelete(DeleteBehavior.Restrict);
            if (!upgraded) entity.Ignore(row => row.Note);
        });
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("Products", table =>
            {
                if (upgraded) table.HasCheckConstraint("CK_Products_Stock", "\"Stock\" >= 0");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Id).ValueGeneratedNever();
            entity.Property(row => row.Name).IsRequired();
            entity.HasOne<Category>().WithMany().HasForeignKey(row => row.CategoryId)
                .HasConstraintName("FK_Products_Category").OnDelete(DeleteBehavior.Restrict);
            if (upgraded) entity.HasIndex(row => row.Name).HasDatabaseName("IX_Products_Name");
        });
        if (upgraded)
        {
            modelBuilder.Entity<Shipment>(entity =>
            {
                entity.ToTable("Shipments");
                entity.HasKey(row => row.Id);
                entity.Property(row => row.Id).ValueGeneratedNever();
                entity.HasOne<Product>().WithMany().HasForeignKey(row => row.ProductId)
                    .HasConstraintName("FK_Shipments_Product").OnDelete(DeleteBehavior.Restrict);
            });
        }
    }
}

/// <summary>具有自引用父分类的库存分类。</summary>
public sealed class Category
{
    /// <summary>分类标识。</summary>
    public long Id { get; set; }
    /// <summary>父分类标识。</summary>
    public long? ParentId { get; set; }
    /// <summary>分类名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>第二次迁移增加的备注。</summary>
    public string? Note { get; set; }
}

/// <summary>库存产品。</summary>
public sealed class Product
{
    /// <summary>产品标识。</summary>
    public long Id { get; set; }
    /// <summary>所属分类。</summary>
    public long CategoryId { get; set; }
    /// <summary>产品名称。</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>当前库存。</summary>
    public long Stock { get; set; }
}

/// <summary>由第二次迁移加入的发货记录。</summary>
public sealed class Shipment
{
    /// <summary>发货标识。</summary>
    public long Id { get; set; }
    /// <summary>关联产品。</summary>
    public long ProductId { get; set; }
    /// <summary>发货数量。</summary>
    public long Quantity { get; set; }
}

/// <summary>库存应用当前关系模型快照。</summary>
[DbContext(typeof(InventoryContext))]
public sealed class InventorySnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder) => InventoryContext.Configure(modelBuilder, upgraded: true);
}
