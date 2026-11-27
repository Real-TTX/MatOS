using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAppNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NodeAddress",
                table: "InstalledApp",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "NodeId",
                table: "InstalledApp",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NodeAddress",
                table: "InstalledApp");

            migrationBuilder.DropColumn(
                name: "NodeId",
                table: "InstalledApp");
        }
    }
}
