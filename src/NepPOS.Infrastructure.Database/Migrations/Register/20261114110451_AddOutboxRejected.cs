using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NepPOS.Infrastructure.Database.Migrations.Register
{
    /// <inheritdoc />
    public partial class AddOutboxRejected : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_SentAtUtc",
                table: "OutboxMessages");

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedAtUtc",
                table: "OutboxMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAtUtc_RejectedAtUtc",
                table: "OutboxMessages",
                columns: new[] { "SentAtUtc", "RejectedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_SentAtUtc_RejectedAtUtc",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "RejectedAtUtc",
                table: "OutboxMessages");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_SentAtUtc",
                table: "OutboxMessages",
                column: "SentAtUtc");
        }
    }
}
