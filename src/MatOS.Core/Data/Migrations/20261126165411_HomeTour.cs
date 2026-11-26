using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class HomeTour : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HomeTourSeenAt",
                table: "User",
                type: "TEXT",
                nullable: true);

            // The accounts that exist already know their home screen: the short tour is for the accounts that come after this version.
            migrationBuilder.Sql("UPDATE \"User\" SET \"HomeTourSeenAt\" = \"CreateDate\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HomeTourSeenAt",
                table: "User");
        }
    }
}
