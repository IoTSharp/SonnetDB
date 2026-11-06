using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryMigrationApp;

/// <summary>建立有自引用和外键的库存基线。</summary>
[DbContext(typeof(InventoryContext))]
[Migration(Id)]
public sealed class InventoryInitial : Migration
{
    /// <summary>初始迁移的固定身份。</summary>
    public const string Id = "20261106000100_InventoryInitial";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("Categories", table => new
        {
            Id = table.Column<long>(type: "INT", nullable: false),
            ParentId = table.Column<long>(type: "INT", nullable: true),
            Name = table.Column<string>(type: "STRING", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Categories", row => row.Id);
            table.ForeignKey("FK_Categories_Parent", row => row.ParentId, "Categories", "Id");
        });
        migrationBuilder.CreateTable("Products", table => new
        {
            Id = table.Column<long>(type: "INT", nullable: false),
            CategoryId = table.Column<long>(type: "INT", nullable: false),
            Name = table.Column<string>(type: "STRING", nullable: false),
            Stock = table.Column<long>(type: "INT", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Products", row => row.Id);
            table.ForeignKey("FK_Products_Category", row => row.CategoryId, "Categories", "Id");
        });
        migrationBuilder.InsertData("Categories", ["Id", "ParentId", "Name"], ["INT", "INT", "STRING"],
            new object[,] { { 1L, 1L, "仓库" }, { 2L, 1L, "配件" } });
        migrationBuilder.InsertData("Products", ["Id", "CategoryId", "Name", "Stock"], ["INT", "INT", "STRING", "INT"],
            new object[,] { { 10L, 2L, "温度传感器", 7L } });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Products");
        migrationBuilder.DropTable("Categories");
    }

    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => InventoryContext.Configure(modelBuilder, upgraded: false);
}

/// <summary>以一个事务增加发货模型、演进旧表并更新库存及迁移历史。</summary>
[DbContext(typeof(InventoryContext))]
[Migration(Id)]
public sealed class InventoryShipping : Migration
{
    /// <summary>发货迁移的固定身份。</summary>
    public const string Id = "20261106000200_InventoryShipping";

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Note", "Categories", type: "STRING", nullable: true);
        migrationBuilder.CreateIndex("IX_Products_Name", "Products", "Name");
        migrationBuilder.AddCheckConstraint("CK_Products_Stock", "Products", "\"Stock\" >= 0");
        migrationBuilder.CreateTable("Shipments", table => new
        {
            Id = table.Column<long>(type: "INT", nullable: false),
            ProductId = table.Column<long>(type: "INT", nullable: false),
            Quantity = table.Column<long>(type: "INT", nullable: false),
        }, constraints: table =>
        {
            table.PrimaryKey("PK_Shipments", row => row.Id);
            table.ForeignKey("FK_Shipments_Product", row => row.ProductId, "Products", "Id");
        });
        migrationBuilder.InsertData("Shipments", ["Id", "ProductId", "Quantity"], ["INT", "INT", "INT"],
            new object[,] { { 100L, 10L, 2L } });
        migrationBuilder.UpdateData("Products", ["Id"], ["INT"], [10L], ["Stock"], ["INT"], [5L]);
        migrationBuilder.UpdateData("Categories", ["Id"], ["INT"], [1L], ["Note"], ["STRING"], ["已迁移✓"]);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("Shipments");
        migrationBuilder.UpdateData("Products", ["Id"], ["INT"], [10L], ["Stock"], ["INT"], [7L]);
        migrationBuilder.DropCheckConstraint("CK_Products_Stock", "Products");
        migrationBuilder.DropIndex("IX_Products_Name", "Products");
        migrationBuilder.DropColumn("Note", "Categories");
    }

    /// <inheritdoc />
    protected override void BuildTargetModel(ModelBuilder modelBuilder) => InventoryContext.Configure(modelBuilder, upgraded: true);
}
