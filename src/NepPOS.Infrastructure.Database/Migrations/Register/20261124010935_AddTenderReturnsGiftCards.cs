using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepPOS.Infrastructure.Database.Migrations.Register
{
    /// <inheritdoc />
    public partial class AddTenderReturnsGiftCards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ReturnOfInvoice",
                table: "SaleTransactions",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReturnOfSaleId",
                table: "SaleTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reference",
                table: "SalePayments",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenderName",
                table: "SalePayments",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GiftCardCode",
                table: "SaleLineItems",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Restock",
                table: "SaleLineItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ReturnOfLineId",
                table: "SaleLineItems",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnReason",
                table: "SaleLineItems",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GiftCards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Balance = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    IssuedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftCards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GiftCardMovements",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GiftCardId = table.Column<int>(type: "INTEGER", nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    SaleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InvoiceNumber = table.Column<long>(type: "INTEGER", nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftCardMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GiftCardMovements_GiftCards_GiftCardId",
                        column: x => x.GiftCardId,
                        principalTable: "GiftCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaleTransactions_ReturnOfSaleId",
                table: "SaleTransactions",
                column: "ReturnOfSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleLineItems_ReturnOfLineId",
                table: "SaleLineItems",
                column: "ReturnOfLineId");

            migrationBuilder.CreateIndex(
                name: "IX_GiftCardMovements_GiftCardId",
                table: "GiftCardMovements",
                column: "GiftCardId");

            migrationBuilder.CreateIndex(
                name: "IX_GiftCards_Code",
                table: "GiftCards",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GiftCardMovements");

            migrationBuilder.DropTable(
                name: "GiftCards");

            migrationBuilder.DropIndex(
                name: "IX_SaleTransactions_ReturnOfSaleId",
                table: "SaleTransactions");

            migrationBuilder.DropIndex(
                name: "IX_SaleLineItems_ReturnOfLineId",
                table: "SaleLineItems");

            migrationBuilder.DropColumn(
                name: "ReturnOfInvoice",
                table: "SaleTransactions");

            migrationBuilder.DropColumn(
                name: "ReturnOfSaleId",
                table: "SaleTransactions");

            migrationBuilder.DropColumn(
                name: "Reference",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "TenderName",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "GiftCardCode",
                table: "SaleLineItems");

            migrationBuilder.DropColumn(
                name: "Restock",
                table: "SaleLineItems");

            migrationBuilder.DropColumn(
                name: "ReturnOfLineId",
                table: "SaleLineItems");

            migrationBuilder.DropColumn(
                name: "ReturnReason",
                table: "SaleLineItems");
        }
    }
}
