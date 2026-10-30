using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmcTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0007_total_project_value : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TotalProjectValue",
                table: "Customers",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Customers_TotalProjectValue_NonNegative",
                table: "Customers",
                sql: "[TotalProjectValue] IS NULL OR [TotalProjectValue] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Customers_TotalProjectValue_NonNegative",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TotalProjectValue",
                table: "Customers");
        }
    }
}
