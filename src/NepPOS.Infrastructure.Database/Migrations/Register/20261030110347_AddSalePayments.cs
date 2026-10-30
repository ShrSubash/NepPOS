using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepPOS.Infrastructure.Database.Migrations.Register
{
    /// <inheritdoc />
    public partial class AddSalePayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CashRounding",
                table: "SaleTransactions",
                type: "TEXT",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "SalePayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SaleTransactionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenderType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Tendered = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    Change = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: false),
                    AuthCode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    CardType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    MaskedPan = table.Column<string>(type: "TEXT", maxLength: 25, nullable: true),
                    ResponseText = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalePayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalePayments_SaleTransactions_SaleTransactionId",
                        column: x => x.SaleTransactionId,
                        principalTable: "SaleTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalePayments_SaleTransactionId",
                table: "SalePayments",
                column: "SaleTransactionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalePayments");

            migrationBuilder.DropColumn(
                name: "CashRounding",
                table: "SaleTransactions");
        }
    }
}
