using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepPOS.Infrastructure.Database.Migrations.Register
{
    /// <inheritdoc />
    public partial class AddReverseDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockReceipts_SupplierId_InvoiceNumber",
                table: "StockReceipts");

            migrationBuilder.AddColumn<string>(
                name: "ReversalReason",
                table: "StockReceipts",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReversedBy",
                table: "StockReceipts",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedUtc",
                table: "StockReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PreviousAverageCost",
                table: "StockReceiptLines",
                type: "TEXT",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PreviousLastCost",
                table: "StockReceiptLines",
                type: "TEXT",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockReceipts_SupplierId_InvoiceNumber",
                table: "StockReceipts",
                columns: new[] { "SupplierId", "InvoiceNumber" },
                unique: true,
                filter: "[ReversedUtc] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockReceipts_SupplierId_InvoiceNumber",
                table: "StockReceipts");

            migrationBuilder.DropColumn(
                name: "ReversalReason",
                table: "StockReceipts");

            migrationBuilder.DropColumn(
                name: "ReversedBy",
                table: "StockReceipts");

            migrationBuilder.DropColumn(
                name: "ReversedUtc",
                table: "StockReceipts");

            migrationBuilder.DropColumn(
                name: "PreviousAverageCost",
                table: "StockReceiptLines");

            migrationBuilder.DropColumn(
                name: "PreviousLastCost",
                table: "StockReceiptLines");

            migrationBuilder.CreateIndex(
                name: "IX_StockReceipts_SupplierId_InvoiceNumber",
                table: "StockReceipts",
                columns: new[] { "SupplierId", "InvoiceNumber" });
        }
    }
}
