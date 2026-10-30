using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AmcTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class _0008_reminder_provider_fields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTest",
                table: "Reminders",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProviderMessageId",
                table: "Reminders",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsTest",
                table: "Reminders");

            migrationBuilder.DropColumn(
                name: "ProviderMessageId",
                table: "Reminders");
        }
    }
}
