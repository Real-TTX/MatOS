using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AccessTokens : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessToken",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Token = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SecretHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Hint = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastUsedFrom = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessToken", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessToken_User_UserId",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessToken_SecretHash",
                table: "AccessToken",
                column: "SecretHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessToken_Token",
                table: "AccessToken",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessToken_UserId",
                table: "AccessToken",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessToken");
        }
    }
}
