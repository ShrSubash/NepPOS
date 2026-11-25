using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepPOS.Infrastructure.Database.Migrations.Register
{
    /// <inheritdoc />
    public partial class AddCardSlips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CustomerReceiptPrintedByTerminal",
                table: "SalePayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MerchantReceipt",
                table: "SalePayments",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CustomerReceiptPrintedByTerminal",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "MerchantReceipt",
                table: "SalePayments");
        }
    }
}
