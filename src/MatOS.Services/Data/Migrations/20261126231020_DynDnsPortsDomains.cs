using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Services.Data.Migrations
{
    /// <inheritdoc />
    public partial class DynDnsPortsDomains : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DynDnsOnly",
                table: "Instance",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastPortCheck",
                table: "Instance",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CustomDomain",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceId = table.Column<long>(type: "INTEGER", nullable: false),
                    Domain = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailingSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomDomain", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomDomain_Instance_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "Instance",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomDomain_Domain",
                table: "CustomDomain",
                column: "Domain",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomDomain_InstanceId",
                table: "CustomDomain",
                column: "InstanceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomDomain");

            migrationBuilder.DropColumn(
                name: "DynDnsOnly",
                table: "Instance");

            migrationBuilder.DropColumn(
                name: "LastPortCheck",
                table: "Instance");
        }
    }
}
