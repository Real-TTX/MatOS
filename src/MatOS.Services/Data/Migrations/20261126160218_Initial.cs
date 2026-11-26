using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Services.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Instance",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PublicIpv4 = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    PublicIpv6 = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedFromIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Instance", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Registration",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Ip = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Registration", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Instance_KeyHash",
                table: "Instance",
                column: "KeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Instance_Name",
                table: "Instance",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Registration_Ip_CreateDate",
                table: "Registration",
                columns: new[] { "Ip", "CreateDate" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Instance");

            migrationBuilder.DropTable(
                name: "Registration");
        }
    }
}
