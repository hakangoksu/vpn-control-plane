using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace VpnControl.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    City = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Country = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    EndpointHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    EndpointPort = table.Column<int>(type: "INTEGER", nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Ipv6Egress = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Peers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ServerId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AssignedAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AssignedAddressV6 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AddressIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Peers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Peers_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Peers_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Devices_TokenHash",
                table: "Devices",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Peers_DeviceId",
                table: "Peers",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_Peers_ServerId_AddressIndex",
                table: "Peers",
                columns: new[] { "ServerId", "AddressIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Peers_ServerId_PublicKey",
                table: "Peers",
                columns: new[] { "ServerId", "PublicKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Servers_AgentTokenHash",
                table: "Servers",
                column: "AgentTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Servers_Country_City",
                table: "Servers",
                columns: new[] { "Country", "City" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Peers");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "Servers");
        }
    }
}
