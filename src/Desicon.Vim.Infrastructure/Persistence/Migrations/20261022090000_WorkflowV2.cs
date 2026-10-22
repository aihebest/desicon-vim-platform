using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Vim.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Workflow v2 (stakeholder meeting, Oct 2026): delivery date for the 3-working-day
    /// target, and the acceptance time from which the vendor's credit period counts.
    /// New statuses (ExceptionReview, Accepted) and the Assisted channel are stored as
    /// strings and need no schema change.
    /// </summary>
    public partial class WorkflowV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcceptedAtUtc",
                schema: "vim",
                table: "Cases",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "DeliveredOn",
                schema: "vim",
                table: "Cases",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AcceptedAtUtc",
                schema: "vim",
                table: "Cases");

            migrationBuilder.DropColumn(
                name: "DeliveredOn",
                schema: "vim",
                table: "Cases");
        }
    }
}
