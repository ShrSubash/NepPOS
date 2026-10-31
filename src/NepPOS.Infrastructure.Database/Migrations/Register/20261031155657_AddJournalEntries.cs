using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepPOS.Infrastructure.Database.Migrations.Register
{
    /// <inheritdoc />
    public partial class AddJournalEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StoreId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RegisterId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EmployeeId = table.Column<int>(type: "INTEGER", nullable: true),
                    EmployeeName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SaleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InvoiceNumber = table.Column<long>(type: "INTEGER", nullable: true),
                    Amount = table.Column<decimal>(type: "TEXT", precision: 18, scale: 2, nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_OccurredAtUtc",
                table: "JournalEntries",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_StoreId_RegisterId_Sequence",
                table: "JournalEntries",
                columns: new[] { "StoreId", "RegisterId", "Sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JournalEntries");
        }
    }
}
