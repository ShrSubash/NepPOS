using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepPOS.Infrastructure.Database.Migrations.Register
{
    /// <inheritdoc />
    public partial class AddStockReceiving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "LastCost",
                table: "Products",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StockReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SupplierId = table.Column<int>(type: "INTEGER", nullable: true),
                    SupplierName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    InvoiceNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    InvoiceDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PostedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EmployeeName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockReceipts_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "StockReceiptLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StockReceiptId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProductName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PackName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PackUnits = table.Column<int>(type: "INTEGER", nullable: false),
                    Quantity = table.Column<decimal>(type: "TEXT", precision: 18, scale: 3, nullable: false),
                    CostPerPack = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: false),
                    PreviousUnitCost = table.Column<decimal>(type: "TEXT", precision: 18, scale: 4, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockReceiptLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StockReceiptLines_StockReceipts_StockReceiptId",
                        column: x => x.StockReceiptId,
                        principalTable: "StockReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockReceiptLines_ProductId",
                table: "StockReceiptLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_StockReceiptLines_StockReceiptId",
                table: "StockReceiptLines",
                column: "StockReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_StockReceipts_SupplierId_InvoiceNumber",
                table: "StockReceipts",
                columns: new[] { "SupplierId", "InvoiceNumber" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockReceiptLines");

            migrationBuilder.DropTable(
                name: "StockReceipts");

            migrationBuilder.DropColumn(
                name: "LastCost",
                table: "Products");
        }
    }
}
