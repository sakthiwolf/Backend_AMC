using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmcTracker.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Module 3, plan-type rules (CLAUDE.md 4.2): AMC = 1 year with the reminder 1 month before the end;
    /// Monthly = 1 month with the reminder 7 days before the end. Status is calculated on read, so the stored
    /// Plans.Status column goes; "Renewed" now means "a plan points back to this one" (unique PreviousPlanId).
    /// Existing plans are recalculated with the new rules (SQL DATEADD clamps to month end like DateOnly).
    /// </summary>
    public partial class _0005_plans_reminders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "NotificationDate",
                table: "Plans",
                newName: "ReminderDate");

            migrationBuilder.RenameIndex(
                name: "IX_Plans_NotificationDate",
                table: "Plans",
                newName: "IX_Plans_ReminderDate");

            // Recalculate every plan from its type and start date (Monthly plans become 1-month plans).
            // Data steps are wrapped in EXEC so the idempotent script (one batch) compiles before the
            // renamed and added columns exist.
            migrationBuilder.Sql(@"EXEC(N'
UPDATE Plans SET EndDate = CASE WHEN PlanType = ''AMC'' THEN DATEADD(YEAR, 1, StartDate)
                                ELSE DATEADD(MONTH, 1, StartDate) END;
UPDATE Plans SET ReminderDate = CASE WHEN PlanType = ''AMC'' THEN DATEADD(MONTH, -1, EndDate)
                                     ELSE DATEADD(DAY, -7, EndDate) END;')");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Plans");

            // One renewal per plan, enforced by the database.
            migrationBuilder.DropIndex(
                name: "IX_Plans_PreviousPlanId",
                table: "Plans");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_PreviousPlanId",
                table: "Plans",
                column: "PreviousPlanId",
                unique: true,
                filter: "[PreviousPlanId] IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Plans_PlanType",
                table: "Plans",
                sql: "[PlanType] IN ('AMC', 'Monthly')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Plans_Amount_NonNegative",
                table: "Plans",
                sql: "[Amount] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Plans_Dates",
                table: "Plans",
                sql: "[StartDate] < [ReminderDate] AND [ReminderDate] < [EndDate]");

            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                table: "Reminders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Rows written before this migration were each tried once.
            migrationBuilder.Sql("EXEC(N'UPDATE Reminders SET Attempts = 1 WHERE Status IN (''Sent'', ''Failed'');')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reminders_Status",
                table: "Reminders",
                sql: "[Status] IN ('Pending', 'Sent', 'Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Reminders_Attempts",
                table: "Reminders",
                sql: "[Attempts] >= 0");

            // The dates shown on each customer come from their current (non-renewed) plan,
            // preferring a plan of the customer's own type.
            migrationBuilder.Sql(@"EXEC(N'
UPDATE c SET c.PlanEndDate = p.EndDate, c.ReminderDate = p.ReminderDate
FROM Customers c
CROSS APPLY (SELECT TOP 1 x.EndDate, x.ReminderDate FROM Plans x
             WHERE x.CustomerId = c.CustomerId
               AND NOT EXISTS (SELECT 1 FROM Plans r WHERE r.PreviousPlanId = x.PlanId)
             ORDER BY CASE WHEN x.PlanType = c.PlanType THEN 0 ELSE 1 END, x.StartDate DESC, x.PlanId DESC) p;')");
        }

        /// <summary>
        /// Puts the columns back. Plan dates stay as recalculated (the old 12-month Monthly dates are not restored);
        /// Status is filled from the dates as of today in Asia/Kolkata.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Reminders_Attempts",
                table: "Reminders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Reminders_Status",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "Attempts",
                table: "Reminders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Plans_Dates",
                table: "Plans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Plans_Amount_NonNegative",
                table: "Plans");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Plans_PlanType",
                table: "Plans");

            migrationBuilder.DropIndex(
                name: "IX_Plans_PreviousPlanId",
                table: "Plans");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_PreviousPlanId",
                table: "Plans",
                column: "PreviousPlanId");

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Plans",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.RenameIndex(
                name: "IX_Plans_ReminderDate",
                table: "Plans",
                newName: "IX_Plans_NotificationDate");

            migrationBuilder.RenameColumn(
                name: "ReminderDate",
                table: "Plans",
                newName: "NotificationDate");

            migrationBuilder.Sql(@"EXEC(N'
DECLARE @today date = CAST(DATEADD(MINUTE, 330, SYSUTCDATETIME()) AS date);
UPDATE p SET Status = CASE
    WHEN EXISTS (SELECT 1 FROM Plans r WHERE r.PreviousPlanId = p.PlanId) THEN ''Renewed''
    WHEN @today < p.NotificationDate THEN ''Active''
    WHEN @today <= p.EndDate THEN ''DueForRenewal''
    ELSE ''Expired'' END
FROM Plans p;')");
        }
    }
}
