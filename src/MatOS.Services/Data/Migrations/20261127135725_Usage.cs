using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MatOS.Services.Data.Migrations
{
    /// <inheritdoc />
    public partial class Usage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiDay",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Registrations = table.Column<long>(type: "INTEGER", nullable: false),
                    GiveBacks = table.Column<long>(type: "INTEGER", nullable: false),
                    PortChecks = table.Column<long>(type: "INTEGER", nullable: false),
                    NameChecks = table.Column<long>(type: "INTEGER", nullable: false),
                    RateLimited = table.Column<long>(type: "INTEGER", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiDay", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DnsDay",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    QueriesA = table.Column<long>(type: "INTEGER", nullable: false),
                    QueriesAaaa = table.Column<long>(type: "INTEGER", nullable: false),
                    QueriesOther = table.Column<long>(type: "INTEGER", nullable: false),
                    NxDomain = table.Column<long>(type: "INTEGER", nullable: false),
                    Updates = table.Column<long>(type: "INTEGER", nullable: false),
                    AddressChanges = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsDay", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DnsHour",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hour = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    QueriesA = table.Column<long>(type: "INTEGER", nullable: false),
                    QueriesAaaa = table.Column<long>(type: "INTEGER", nullable: false),
                    QueriesOther = table.Column<long>(type: "INTEGER", nullable: false),
                    NxDomain = table.Column<long>(type: "INTEGER", nullable: false),
                    Updates = table.Column<long>(type: "INTEGER", nullable: false),
                    AddressChanges = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsHour", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TunnelSession",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InstanceName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DisconnectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    RemoteAddress = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    Version = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    StreamsServed = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesToInstance = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesToVisitor = table.Column<long>(type: "INTEGER", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TunnelSession", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageDay",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Day = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Way = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Connections = table.Column<long>(type: "INTEGER", nullable: false),
                    RefusedNoTunnel = table.Column<long>(type: "INTEGER", nullable: false),
                    RefusedLimit = table.Column<long>(type: "INTEGER", nullable: false),
                    RefusedTimeout = table.Column<long>(type: "INTEGER", nullable: false),
                    RefusedBlocked = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesToInstance = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesToVisitor = table.Column<long>(type: "INTEGER", nullable: false),
                    ConnectionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    PeakStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    Visitors = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageDay", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UsageHour",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Hour = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateState = table.Column<int>(type: "INTEGER", nullable: false),
                    InstanceName = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 253, nullable: false),
                    Way = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    Connections = table.Column<long>(type: "INTEGER", nullable: false),
                    RefusedNoTunnel = table.Column<long>(type: "INTEGER", nullable: false),
                    RefusedLimit = table.Column<long>(type: "INTEGER", nullable: false),
                    RefusedTimeout = table.Column<long>(type: "INTEGER", nullable: false),
                    RefusedBlocked = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesToInstance = table.Column<long>(type: "INTEGER", nullable: false),
                    BytesToVisitor = table.Column<long>(type: "INTEGER", nullable: false),
                    ConnectionSeconds = table.Column<long>(type: "INTEGER", nullable: false),
                    PeakStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    Visitors = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageHour", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiDay_Day",
                table: "ApiDay",
                column: "Day",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DnsDay_Day_Name",
                table: "DnsDay",
                columns: new[] { "Day", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DnsDay_Name_Day",
                table: "DnsDay",
                columns: new[] { "Name", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_DnsHour_Hour_Name",
                table: "DnsHour",
                columns: new[] { "Hour", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DnsHour_Name_Hour",
                table: "DnsHour",
                columns: new[] { "Name", "Hour" });

            migrationBuilder.CreateIndex(
                name: "IX_TunnelSession_DisconnectedAt",
                table: "TunnelSession",
                column: "DisconnectedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TunnelSession_InstanceName_ConnectedAt",
                table: "TunnelSession",
                columns: new[] { "InstanceName", "ConnectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageDay_Day_InstanceName_Host_Way",
                table: "UsageDay",
                columns: new[] { "Day", "InstanceName", "Host", "Way" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageDay_InstanceName_Day",
                table: "UsageDay",
                columns: new[] { "InstanceName", "Day" });

            migrationBuilder.CreateIndex(
                name: "IX_UsageHour_Hour_InstanceName_Host_Way",
                table: "UsageHour",
                columns: new[] { "Hour", "InstanceName", "Host", "Way" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageHour_InstanceName_Hour",
                table: "UsageHour",
                columns: new[] { "InstanceName", "Hour" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiDay");

            migrationBuilder.DropTable(
                name: "DnsDay");

            migrationBuilder.DropTable(
                name: "DnsHour");

            migrationBuilder.DropTable(
                name: "TunnelSession");

            migrationBuilder.DropTable(
                name: "UsageDay");

            migrationBuilder.DropTable(
                name: "UsageHour");
        }
    }
}
