using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmcTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0006_payments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The Payments table has existed (empty) since the first migration. Mode becomes required and gets
            // checks, so stop with a clear message if any old row could not satisfy them.
            migrationBuilder.Sql(@"
IF EXISTS (SELECT 1 FROM Payments WHERE Mode IS NULL OR Mode NOT IN ('Cash', 'UPI', 'Bank') OR Amount <= 0)
    THROW 50006, 'Payments has rows without a Cash/UPI/Bank mode or with an amount of 0 or less. Fix or remove them, then run this migration again.', 1;");

            migrationBuilder.AlterColumn<string>(
                name: "Mode",
                table: "Payments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20,
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Payments",
                type: "datetime2",
                nullable: false,
                defaultValueSql: "SYSUTCDATETIME()");

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Payments",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payments_Amount_Positive",
                table: "Payments",
                sql: "[Amount] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Payments_Mode",
                table: "Payments",
                sql: "[Mode] IN ('Cash', 'UPI', 'Bank')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Amount_Positive",
                table: "Payments");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Payments_Mode",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "Payments");

            migrationBuilder.AlterColumn<string>(
                name: "Mode",
                table: "Payments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(20)",
                oldMaxLength: 20);
        }
    }
}
