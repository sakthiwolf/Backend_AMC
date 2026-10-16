using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmcTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0002_customers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Customers_CustomerName",
                table: "Customers",
                column: "CustomerName");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_IsActive",
                table: "Customers",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_Phone",
                table: "Customers",
                column: "Phone");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Customers_Phone_E164",
                table: "Customers",
                sql: "LEFT([Phone], 1) = '+' AND LEN([Phone]) BETWEEN 9 AND 16 AND SUBSTRING([Phone], 2, 1) <> '0' AND SUBSTRING([Phone], 2, 20) NOT LIKE '%[^0-9]%'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Customers_ProjectValue_NonNegative",
                table: "Customers",
                sql: "[ProjectValue] IS NULL OR [ProjectValue] >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_CustomerName",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_IsActive",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Customers_Phone",
                table: "Customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Customers_Phone_E164",
                table: "Customers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Customers_ProjectValue_NonNegative",
                table: "Customers");
        }
    }
}
