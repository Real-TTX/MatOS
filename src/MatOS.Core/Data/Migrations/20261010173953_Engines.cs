using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class Engines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComposeSha",
                table: "InstalledApp",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Engine",
                table: "InstalledApp",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "local");

            migrationBuilder.AddColumn<bool>(
                name: "IsCore",
                table: "InstalledApp",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComposeSha",
                table: "InstalledApp");

            migrationBuilder.DropColumn(
                name: "Engine",
                table: "InstalledApp");

            migrationBuilder.DropColumn(
                name: "IsCore",
                table: "InstalledApp");
        }
    }
}
