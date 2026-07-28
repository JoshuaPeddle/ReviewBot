using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ReviewBot.Persistence.Migrations;

[DbContext(typeof(ReviewBotDbContext))]
[Migration("20260523000000_InitialCreate")]
public partial class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Deliveries",
            columns: table => new
            {
                DeliveryId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                ProcessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Deliveries", x => x.DeliveryId);
            });

        migrationBuilder.CreateIndex(
            name: "IX_Deliveries_ProcessedAt",
            table: "Deliveries",
            column: "ProcessedAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "Deliveries");
    }
}
