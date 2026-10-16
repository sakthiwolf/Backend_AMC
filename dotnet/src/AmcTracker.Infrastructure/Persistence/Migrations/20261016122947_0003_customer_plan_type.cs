using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmcTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0003_customer_plan_type : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PlanType",
                table: "Customers",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "AMC");

            // Existing customers take the type of their newest open plan.
            migrationBuilder.Sql(@"
UPDATE c SET c.PlanType = p.PlanType
FROM Customers c
CROSS APPLY (SELECT TOP 1 PlanType FROM Plans
             WHERE CustomerId = c.CustomerId AND Status <> 'Renewed'
             ORDER BY StartDate DESC, PlanId DESC) p;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Customers_PlanType",
                table: "Customers",
                sql: "[PlanType] IN ('AMC', 'Monthly')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Customers_PlanType",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "PlanType",
                table: "Customers");
        }
    }
}
