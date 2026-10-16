using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmcTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0004_customer_plan_dates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "PlanEndDate",
                table: "Customers",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReminderDate",
                table: "Customers",
                type: "date",
                nullable: true);

            // Fill the new dates for existing customers from their newest open plan.
            migrationBuilder.Sql(@"
UPDATE c SET c.PlanEndDate = p.EndDate,
             c.ReminderDate = CASE
                 WHEN p.PlanType = 'AMC' THEN CASE WHEN p.NotificationDate >= CAST(SYSUTCDATETIME() AS date) THEN p.NotificationDate END
                 ELSE (SELECT TOP 1 DATEADD(DAY, -7, DATEADD(MONTH, v.n, p.StartDate))
                       FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),(11),(12)) v(n)
                       WHERE DATEADD(DAY, -7, DATEADD(MONTH, v.n, p.StartDate)) >= CAST(SYSUTCDATETIME() AS date)
                       ORDER BY v.n) END
FROM Customers c
CROSS APPLY (SELECT TOP 1 * FROM Plans
             WHERE CustomerId = c.CustomerId AND Status <> 'Renewed'
             ORDER BY StartDate DESC, PlanId DESC) p;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PlanEndDate",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ReminderDate",
                table: "Customers");
        }
    }
}
