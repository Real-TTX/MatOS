using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Services.Data.Migrations
{
    /// <inheritdoc />
    public partial class FirstTunnel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstTunnel",
                table: "Instance",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstTunnel",
                table: "Instance");
        }
    }
}
