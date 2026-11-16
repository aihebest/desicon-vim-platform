using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Desicon.Vim.Infrastructure.Persistence.Migrations
{
    /// <summary>Advance payment control: the advance the submitter declares as already paid on the order.</summary>
    public partial class AdvanceDeclared : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeclaredAdvance",
                schema: "vim",
                table: "Cases",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeclaredAdvance",
                schema: "vim",
                table: "Cases");
        }
    }
}
