using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace HeatSynQ.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlatformConcurrencyTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                schema: "platform",
                table: "retention_policies",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                schema: "platform",
                table: "number_sequences",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                schema: "platform",
                table: "legal_holds",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "Version",
                schema: "platform",
                table: "facility_settings",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                schema: "platform",
                table: "retention_policies");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "platform",
                table: "number_sequences");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "platform",
                table: "legal_holds");

            migrationBuilder.DropColumn(
                name: "Version",
                schema: "platform",
                table: "facility_settings");
        }
    }
}
