using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AppBindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppBinding",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConsumerAppId = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderAppId = table.Column<long>(type: "INTEGER", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    VariablesProtected = table.Column<string>(type: "TEXT", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppBinding", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppBinding_InstalledApp_ConsumerAppId",
                        column: x => x.ConsumerAppId,
                        principalTable: "InstalledApp",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppBinding_InstalledApp_ProviderAppId",
                        column: x => x.ProviderAppId,
                        principalTable: "InstalledApp",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AppBinding_ConsumerAppId_Capability",
                table: "AppBinding",
                columns: new[] { "ConsumerAppId", "Capability" });

            migrationBuilder.CreateIndex(
                name: "IX_AppBinding_ProviderAppId",
                table: "AppBinding",
                column: "ProviderAppId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppBinding");
        }
    }
}
