using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Vim.Infrastructure.Persistence.Migrations
{
    /// <summary>Vendor portal phase 1: invitations, portal users bound to a BC vendor, and their access log.</summary>
    public partial class VendorAccess : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VendorAccessEvents",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    VendorNo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ActorId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActorName = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Details = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorAccessEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorAccessEvents_VendorNo_OccurredAtUtc",
                schema: "vim",
                table: "VendorAccessEvents",
                columns: new[] { "VendorNo", "OccurredAtUtc" });

            migrationBuilder.CreateTable(
                name: "VendorInvitations",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VendorNo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VendorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Confirmation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConfirmationNote = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedByName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RevokedById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorInvitations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorInvitations_TokenHash",
                schema: "vim",
                table: "VendorInvitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorInvitations_VendorNo_Email",
                schema: "vim",
                table: "VendorInvitations",
                columns: new[] { "VendorNo", "Email" });

            migrationBuilder.CreateTable(
                name: "VendorUsers",
                schema: "vim",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExternalUserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    VendorNo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    VendorName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastSignInAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisabledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DisabledById = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    DisabledReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorUsers", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorUsers_ExternalUserId",
                schema: "vim",
                table: "VendorUsers",
                column: "ExternalUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorUsers_VendorNo",
                schema: "vim",
                table: "VendorUsers",
                column: "VendorNo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendorAccessEvents",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "VendorInvitations",
                schema: "vim");

            migrationBuilder.DropTable(
                name: "VendorUsers",
                schema: "vim");
        }
    }
}
